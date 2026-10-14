"""Read-only, credential-free contract probes. Never follows redirects or saves raw bodies/headers."""
import concurrent.futures
import datetime
import hashlib
import json
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

TARGETS = [
    ("steam-search", "https://store.steampowered.com/api/storesearch/?term=Portal&l=english&cc=US", 8),
    ("steam-featured", "https://store.steampowered.com/api/featuredcategories?cc=us&l=english", 8),
    ("steam-details", "https://store.steampowered.com/api/appdetails?appids=620&cc=us&l=english", 8),
    ("morrenus-applist", "https://applist.morrenus.xyz/", 64),
    ("steam-applist", "https://api.steampowered.com/ISteamApps/GetAppList/v2/", 64),
    ("steamcmd-info", "https://api.steamcmd.net/v1/info/620", 8),
    ("lua-listings", "https://lua.tools/api/denuvo/listings", 8),
    ("lua-fixes", "https://lua.tools/api/denuvo/fixes?appid=620", 8),
    ("hardware-list", "https://raw.githubusercontent.com/jsnli/steamappidlist/master/data/hardware_appid.json", 8),
    ("depot-release", "https://api.github.com/repos/mendy-tools/DepotDownloaderMod/releases/latest", 8),
    ("plugin-release", "https://api.github.com/repos/madoiscool/LTSP/releases/latest", 8),
]

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

def summarize(value):
    origins = set()
    def visit(item, depth=0):
        if depth > 16:
            return
        if isinstance(item, dict):
            for child in item.values():
                visit(child, depth + 1)
        elif isinstance(item, list):
            for child in item[:100]:
                visit(child, depth + 1)
        elif isinstance(item, str) and item.startswith(("https://", "http://")):
            parsed = urllib.parse.urlsplit(item)
            if parsed.hostname:
                origins.add(f"{parsed.scheme}://{parsed.hostname}")
    visit(value)
    return {"json_type": type(value).__name__,
            "top_level_keys": sorted(value.keys())[:32] if isinstance(value, dict) else None,
            "item_count": len(value) if isinstance(value, (dict, list)) else None,
            "sample_url_origins": sorted(origins)}

def probe(target):
    name, url, mib = target
    record = {"name": name, "method": "GET", "url": url,
              "authentication": "none", "redirects": "disabled"}
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    request = urllib.request.Request(url, headers={"User-Agent": "LuaTools-contract-probe", "Accept": "application/json"})
    try:
        try:
            response = opener.open(request, timeout=15)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            record["status"] = response.status
            record["content_type"] = response.headers.get("Content-Type", "")
            record["declared_bytes"] = response.headers.get("Content-Length")
            location = response.headers.get("Location")
            if location:
                parsed = urllib.parse.urlsplit(urllib.parse.urljoin(url, location))
                record["redirect_origin"] = f"{parsed.scheme}://{parsed.hostname}"
            data = response.read(mib * 1024 * 1024 + 1)
            record["observed_bytes"] = len(data)
            record["over_limit"] = len(data) > mib * 1024 * 1024
            record["sha256"] = hashlib.sha256(data).hexdigest()
            if not record["over_limit"]:
                try:
                    record.update(summarize(json.loads(data)))
                except (ValueError, RecursionError):
                    record["json_parse"] = "rejected"
    except Exception as error:
        record["transport_error"] = type(error).__name__
    return record

if __name__ == "__main__":
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        records = list(pool.map(probe, TARGETS))
    result = {"observed_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "notes": "Public read-only probes; no auth, cookies, plaintext requests, redirects or executable asset downloads. Raw bodies and response headers are not retained. Errors are evidence of this probe only.",
              "exchanges": records}
    destination = Path(__file__).resolve().parent.parent / "evidence" / "public-http.json"
    destination.parent.mkdir(exist_ok=True)
    destination.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    for record in records:
        print(record["name"], record.get("status", record.get("transport_error")), record.get("observed_bytes", 0))
