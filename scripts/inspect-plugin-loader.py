"""Static PE inspection; never loads or executes the input DLL.

Requires pefile and capstone. Usage:
  python scripts/inspect-plugin-loader.py path/to/winmm.dll artifacts/loader-review
"""
import hashlib
import json
from pathlib import Path
import re
import struct
import sys

import capstone
import pefile

EXPECTED = "dc1594774d3003f7c82fbcdaac4cc9bbc81d7ee2ab82dd5f47c4f04cc3bd8236"


def main():
    source, output = Path(sys.argv[1]), Path(sys.argv[2])
    data = source.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != EXPECTED:
        raise ValueError("Input differs from the reviewed LTSP v2.2 loader")
    pe = pefile.PE(data=data)
    base = pe.OPTIONAL_HEADER.ImageBase
    imports = {i.address: (i.name.decode() if i.name else str(i.ordinal))
               for module in pe.DIRECTORY_ENTRY_IMPORT for i in module.imports}
    exports = [{"name": s.name.decode() if s.name else None, "rva": hex(s.address),
                "forwarder": s.forwarder.decode() if s.forwarder else None}
               for s in pe.DIRECTORY_ENTRY_EXPORT.symbols]
    callbacks = []
    if hasattr(pe, "DIRECTORY_ENTRY_TLS"):
        rva = pe.DIRECTORY_ENTRY_TLS.struct.AddressOfCallBacks - base
        for index in range(64):
            address = struct.unpack("<Q", pe.get_data(rva + index * 8, 8))[0]
            if not address:
                break
            callbacks.append(hex(address - base))
    strings = []
    for encoding, pattern in [("ascii", rb"[\x20-\x7e]{6,}"),
                              ("utf-16le", rb"(?:[\x20-\x7e]\x00){6,}")]:
        for match in re.finditer(pattern, data):
            strings.append({"rva": hex(pe.get_rva_from_offset(match.start())),
                            "encoding": encoding, "text": match.group().decode(encoding)})
    disassembler = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    disassembler.detail = True
    assembly = []
    for section in pe.sections:
        if not section.Characteristics & 0x20000000:
            continue
        for instruction in disassembler.disasm(section.get_data(), base + section.VirtualAddress):
            annotations = []
            for operand in instruction.operands:
                if operand.type == capstone.x86.X86_OP_MEM and operand.mem.base == capstone.x86.X86_REG_RIP:
                    target = instruction.address + instruction.size + operand.mem.disp
                    if target in imports:
                        annotations.append(imports[target])
                    else:
                        annotations.append("rip-target-rva=" + hex(target - base))
            assembly.append(f"{instruction.address-base:08x}: {instruction.mnemonic:8} {instruction.op_str}"
                            + (" ; " + ", ".join(annotations) if annotations else ""))
    report = {"sha256": digest, "size": len(data), "machine": hex(pe.FILE_HEADER.Machine),
              "entryRva": hex(pe.OPTIONAL_HEADER.AddressOfEntryPoint), "imageBase": hex(base),
              "embeddedCertificateBytes": pe.OPTIONAL_HEADER.DATA_DIRECTORY[4].Size,
              "tlsCallbackRvas": callbacks,
              "imports": {m.dll.decode(): [imports[i.address] for i in m.imports]
                          for m in pe.DIRECTORY_ENTRY_IMPORT}, "exports": exports}
    output.mkdir(parents=True, exist_ok=True)
    (output / "pe.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    (output / "strings.json").write_text(json.dumps(strings, indent=2) + "\n", encoding="utf-8")
    (output / "disassembly.txt").write_text("\n".join(assembly) + "\n", encoding="utf-8")
    print(f"Inspected {digest}; {len(exports)} exports; input was not executed.")


if __name__ == "__main__":
    main()
