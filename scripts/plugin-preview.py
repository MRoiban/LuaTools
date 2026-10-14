"""Build an offline visual fixture around the actual bundled plugin sources.

Only the location and bridge are simulated. Production JS/CSS are unmodified.
Run: python scripts/plugin-preview.py --installed
"""
import argparse
import json
import re
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--installed', action='store_true')
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
html = (root / 'design/steam-plugin/mockup.html').read_text(encoding='utf-8')
html = html[:html.index('<script>')]
html = re.sub(r'<div class="dz".*?</div>\s*<div class="frame"', '<div class="frame"', html, flags=re.S)
html = re.sub(r'<button class="lt-iconbtn.*?</button>', '<div class="_1wn1lBlAzl3HMRqS1llwie"></div>', html, flags=re.S)
html = html.replace('<div id="lt-root" aria-live="polite"></div>', '<div id="game_area_purchase"></div>')
js = (root / 'src/LuaToolsGui/Plugin/steam-plugin.js').read_text(encoding='utf-8')
css = (root / 'src/LuaToolsGui/Plugin/steam-plugin.css').read_text(encoding='utf-8')
script = '''
window.fixtureCalls=[];
window.fixtureState={success:true,checking:false,fastFetch:false,sourcesLoaded:true,sources:[
 {name:'Sadie (Morrenus)',displayName:'Sadie',status:'available',canDownload:true,stats:'12/25'},
 {name:'Ryuu',displayName:'Ryuu',status:'available',canDownload:true,stats:'3/25'},
 {name:'Kernel',displayName:'Kernel',status:'unavailable',canDownload:false},
 {name:'Locked',displayName:'Locked',status:'unknown',needsKey:true,locked:true,canDownload:false}]};
window.LuaToolsBridge={call:async(method,args)=>{
 fixtureCalls.push({method,args});
 if(args.appid !== undefined && (typeof args.appid !== 'number' || args.appid!==1145360)) throw Error('Invalid numeric appid');
 if(method==='HasLuaToolsForApp') return {success:true,exists:INSTALLED};
 if(method==='GetLuaToolsAddStatus') return fixtureState;
 if(method==='PickLuaToolsAddSource') fixtureState={...fixtureState,sources:[{name:'Ryuu',displayName:'Ryuu',downloading:true,progress:42}]};
 if(method==='ReadLoadedApps') return {success:true,apps:[]};
 return {success:true};
}};
'''.replace('INSTALLED', str(args.installed).lower())
script += '\nwindow.__LuaToolsCss=' + json.dumps(css) + ';\n'
script += '(function(location){\n' + js + '\n})({hostname:"store.steampowered.com", pathname:"/app/1145360", href:"https://store.steampowered.com/app/1145360/"});'
html += '<script>' + script.replace('</script', '<\\/script') + '</script></body></html>'
output = root / 'artifacts/plugin-preview'
output.mkdir(parents=True, exist_ok=True)
(output / 'index.html').write_text(html, encoding='utf-8')
print(output / 'index.html')
