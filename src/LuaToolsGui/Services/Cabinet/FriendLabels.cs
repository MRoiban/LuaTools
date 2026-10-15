using System.Globalization;
using System.Text.RegularExpressions;
namespace LuaToolsGui.Services.Cabinet;

internal static class FriendLabels
{
    private static string _script = BuildScript(new());
    private static long _expires;
    internal static bool Active => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < Interlocked.Read(ref _expires);
    internal static string Script => Active ? Volatile.Read(ref _script) : BuildScript(new());
    internal static void Clear() { Interlocked.Exchange(ref _expires, 0); Volatile.Write(ref _script, BuildScript(new())); }
    internal static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= 100 ? clean : clean[..(char.IsHighSurrogate(clean[99]) ? 99 : 100)];
    }
    internal sealed class Label { public string name { get; set; } = ""; public string app { get; set; } = ""; }
    internal static Dictionary<string, Label> ReadPlayers(object response, uint own)
    {
        var result = new Dictionary<string, Label>();
        if (!CabinetCloudClient.Map(response).TryGetValue("players", out var players) || players is not object[] list || list.Length > 4096) return result;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var entry in list)
        {
            var row = CabinetCloudClient.Map(entry);
            if (!row.TryGetValue("accountId", out var account) || !uint.TryParse(Convert.ToString(account, CultureInfo.InvariantCulture), out var id) || id == 0 || id == own
                || !row.TryGetValue("updatedAt", out var updated) || !long.TryParse(Convert.ToString(updated, CultureInfo.InvariantCulture), out var time)
                || time < now - 180000 || time > now + 5000 || !row.TryGetValue("name", out var name) || name is not string
                || !row.TryGetValue("appId", out var app) || app is not string appId || !Regex.IsMatch(appId, @"\A[1-9][0-9]{0,9}\z")) continue;
            var clean = CleanName((string)name);
            if (clean.Length > 0) result[id.ToString(CultureInfo.InvariantCulture)] = new Label { name = clean, app = appId };
        }
        return result;
    }
    internal static void Update(object response, uint own)
    {
        var labels = ReadPlayers(response, own);
        long expires = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 180000;
        if (CabinetCloudClient.Map(response).TryGetValue("players", out var players) && players is object[] list)
            foreach (var entry in list)
            {
                var row = CabinetCloudClient.Map(entry);
                if (row.TryGetValue("accountId", out var id) && labels.ContainsKey(Convert.ToString(id, CultureInfo.InvariantCulture)!)
                    && row.TryGetValue("updatedAt", out var time)) expires = Math.Min(expires, Convert.ToInt64(time) + 180000);
            }
        Volatile.Write(ref _script, BuildScript(labels).Replace("s.expires=Date.now()+180000", "s.expires=" + expires.ToString(CultureInfo.InvariantCulture)));
        Interlocked.Exchange(ref _expires, expires);
    }
    internal static string BuildScript(Dictionary<string, Label> labels)
    {
            return @"(function(labels,fallback){
var store=window.g_FriendsUIApp&&g_FriendsUIApp.FriendStore;
if(!store)throw new Error('Cabinet: friends store unavailable');
var s=window.__cabinetLabels;
if(s&&s.version!==6){
 try{s.labels={};s.written.forEach(function(v,p){s.clear(p);});}catch(e){}
 clearInterval(s.timer);if(s.pending)clearTimeout(s.pending);
 s.patches.forEach(function(v,p){if(v.wrapped){if(p.UpdateFromMessage===v.wrapped)p.UpdateFromMessage=v.original;}
  else Object.keys(v).forEach(function(n){if(p[n]===v[n].wrapped)p[n]=v[n].original;});});
 var w=s.spacewarInfo;
 if(w&&w.copy){if(w.map.get(480)===w.copy)w.map.set(480,w.original);}else if(w)delete w.icon_url;
 if(s.unpatchGroups)try{s.unpatchGroups();}catch(e){}
 // Version 5 stopped halfway on Steam's MobX store and left these behind.
 var gs=store.FriendGroupStore;
 if(gs)['GetGameGroup','GetCountFriendsInGame','games_with_friends_playing'].forEach(function(n){var o=Object.getOwnPropertyDescriptor(gs,n);
  if(o&&o.configurable&&/s\.moved|g\.GetGameGroup/.test(String(o.value||o.get)))delete gs[n];});
 s=null;
}
if(!s){
 s=window.__cabinetLabels={version:6,labels:{},written:new Map(),patches:new Map(),friends:new Map(),errors:{},expires:0,fallback:'',sig:''};
 // Every hook runs inside Steam's own code: a failure is recorded and Steam's own result is used.
 s.fail=function(n,e){s.errors[n]=String(e&&e.message||e).slice(0,200);};
 s.step=function(n,f){try{f();delete s.errors[n];}catch(e){s.fail(n,e);}};
 // Friends relabeled to a game in this library are counted under that game (library
 // page, green badge, home carousel) instead of Spacewar. Not in the library: Spacewar.
 s.owned=function(app){var st=window.appStore;try{return !!(st&&st.GetAppOverviewByAppID&&st.GetAppOverviewByAppID(Number(app)));}catch(e){return false;}};
 s.moved=function(){var by={};s.written.forEach(function(v,p){
   if(v.friend&&p.m_unGamePlayedAppID===480&&p.m_gameid===v.app&&s.owned(v.app))(by[v.app]=by[v.app]||[]).push(v.friend);});return by;};
 s.group=function(list){return {member_list:list,member_list_unsorted:list,unfiltered_count:list.length,member_count:list.length};};
 s.patchGroups=function(){var g=store.FriendGroupStore;if(!g||(s.groupStore&&s.groupStore.store===g))return;
  if(s.groupStore)s.unpatchGroups();
  var proto=Object.getPrototypeOf(g),map=g.m_mapGameGroups,d=null,get=proto.GetGameGroup,count=proto.GetCountFriendsInGame;
  for(var o=proto;o&&!d;o=Object.getPrototypeOf(o))d=Object.getOwnPropertyDescriptor(o,'games_with_friends_playing');
  if(!map||typeof map.get!=='function'||typeof get!=='function'||!d||!d.get)return;
  var u={store:g};
  var keys=function(k){try{var r=k.filter(function(x){return Number(x)!==480||!!g.GetGameGroup(480);});
    Object.keys(s.moved()).forEach(function(a){if(r.indexOf(Number(a))<0)r.push(Number(a));});return r;}catch(e){s.fail('games',e);return k;}};
  g.GetGameGroup=function(e){var base=get.call(g,e);try{var by=s.moved(),extra=by[String(e)];
    if(Number(e)===480&&base){var all=[];Object.keys(by).forEach(function(k){all=all.concat(by[k]);});
     if(!all.length)return base;var left=base.member_list.filter(function(f){return all.indexOf(f)<0;});return left.length?s.group(left):undefined;}
    return extra?s.group((base?base.member_list:[]).concat(extra)):base;}catch(x){s.fail('GetGameGroup',x);return base;}};
  g.GetCountFriendsInGame=function(e){try{var r=g.GetGameGroup(e);return r?r.unfiltered_count||0:0;}
    catch(x){s.fail('count',x);return typeof count==='function'?count.call(g,e):0;}};
  var own=Object.getOwnPropertyDescriptor(g,'games_with_friends_playing');
  if(own&&!own.configurable){
   // Steam's store is MobX: the property is a non-configurable computed, so its derivation is wrapped.
   var sym=Object.getOwnPropertySymbols(g).filter(function(x){return String(x)==='Symbol(mobx administration)';})[0],adm=sym&&g[sym],
    cv=adm&&adm.values_&&typeof adm.values_.get==='function'&&adm.values_.get('games_with_friends_playing');
   if(cv&&typeof cv.derivation==='function'){var od=cv.derivation;cv.derivation=function(){return keys(od.call(this));};u.computed={cv:cv,original:od,wrapped:cv.derivation};}
  }else Object.defineProperty(g,'games_with_friends_playing',{configurable:true,get:function(){return keys(d.get.call(this));}});
  s.groupStore=u;};
 s.unpatchGroups=function(){var u=s.groupStore;if(!u)return;s.groupStore=null;delete u.store.GetGameGroup;delete u.store.GetCountFriendsInGame;
  var o=Object.getOwnPropertyDescriptor(u.store,'games_with_friends_playing');if(o&&o.configurable)delete u.store.games_with_friends_playing;
  if(u.computed&&u.computed.cv.derivation===u.computed.wrapped)u.computed.cv.derivation=u.computed.original;};
 // Components already rendered observe map keys, not our patch: touch the affected keys.
 s.nudge=function(){var w=s.groupStore&&s.groupStore.store.m_mapGameGroups;if(!w)return;var by=s.moved(),sig=JSON.stringify(Object.keys(by).map(function(k){return k+':'+by[k].length;}));
  if(sig===s.sig)return;var keys=[480].concat(Object.keys(by).map(Number),s.sigKeys||[]);s.sig=sig;s.sigKeys=Object.keys(by).map(Number);
  var any=w.get(480)||Array.from(w.values())[0];
  keys.forEach(function(k){if(w.has(k)){var x=w.get(k);w.delete(k);w.set(k,x);}else if(any){w.set(k,any);w.delete(k);}});};
 s.spacewarIcon=function(){var apps=window.g_FriendsUIApp&&g_FriendsUIApp.AppInfoStore,current=window.g_FriendsUIApp&&g_FriendsUIApp.FriendStore;
  if(!apps||!current)return null;
  var games=new Set(),unknown=false;
  Array.from(current.all_friends).forEach(function(f){var p=f.persona;if(!p||p.m_unGamePlayedAppID!==480)return;
   var v=s.written.get(p);if(v&&p.m_gameid===v.app)games.add(v.app);else unknown=true;});
  if(games.size===1&&!unknown){var info=apps.GetAppInfo(Number(games.values().next().value));if(info&&info.m_unAppID!==480&&info.icon_url)return info.icon_url;}
  return s.fallback||null;};
 // AppInfo objects aren't observable; the store's map is. Swapping in a copy with the
 // icon override makes rendered badges refresh; restoring the original undoes it.
 s.patchSpacewar=function(){var apps=window.g_FriendsUIApp&&g_FriendsUIApp.AppInfoStore,map=apps&&apps.m_mapAppInfo;
  if(!map||typeof map.set!=='function')return;apps.GetAppInfo(480);var a=map.get(480);
  if(!a||(s.spacewarInfo&&a===s.spacewarInfo.copy))return;
  var d=Object.getOwnPropertyDescriptor(Object.getPrototypeOf(a),'icon_url');if(!d||!d.get)return;
  var c=Object.create(Object.getPrototypeOf(a));Object.getOwnPropertyNames(a).forEach(function(k){c[k]=a[k];});
  Object.defineProperty(c,'icon_url',{configurable:true,get:function(){try{var i=s.spacewarIcon();if(i)return i;}catch(e){s.fail('spacewarIcon',e);}return d.get.call(this);}});
  s.spacewarInfo={map:map,original:a,copy:c};map.set(480,c);};
 s.clear=function(p){var v=s.written.get(p);if(!v)return;
  if(p.m_strGameExtraInfo===v.name)p.m_strGameExtraInfo=v.beforeName;
  if(p.m_gameid===v.app)p.m_gameid=v.beforeGame;
  s.written.delete(p);};
 s.one=function(p,id,f){var l=s.labels[String(id)];
  if(p.m_unGamePlayedAppID!==480||!l){s.clear(p);return;}
  var v=s.written.get(p);
  if(!v||p.m_strGameExtraInfo!==v.name||p.m_gameid!==v.app){v={beforeName:p.m_strGameExtraInfo,beforeGame:p.m_gameid};s.written.set(p,v);}
  v.name=l.name;v.app=l.app;v.friend=f;p.m_strGameExtraInfo=l.name;p.m_gameid=l.app;};
 s.icon=function(p){var v=s.written.get(p),apps=window.g_FriendsUIApp&&g_FriendsUIApp.AppInfoStore;
  if(!v||p.m_gameid!==v.app||!apps)return null;
  var info=apps.GetAppInfo(Number(v.app));return info&&info.icon_url?info.icon_url:null;};
 s.patch=function(proto,name,make){var m=s.patches.get(proto);if(!m){m={};s.patches.set(proto,m);}
  if(m[name]||typeof proto[name]!=='function')return;
  var original=proto[name],wrapped=make(original);m[name]={original:original,wrapped:wrapped};proto[name]=wrapped;};
 s.labelAll=function(){
  if(Date.now()>s.expires)s.labels={};
  var current=window.g_FriendsUIApp&&g_FriendsUIApp.FriendStore;if(!current)return;
  var friends=Array.from(current.all_friends),ids=new Map();
  friends.forEach(function(f){if(f.persona)ids.set(f.persona,f);});
  s.written.forEach(function(v,p){if(!ids.has(p))s.clear(p);});s.friends=ids;
  friends.forEach(function(f){var p=f.persona;if(!p)return;
   var proto=Object.getPrototypeOf(p);
   if(proto){
    // The updated persona is relabeled at once; store-wide work runs once per burst.
    s.patch(proto,'UpdateFromMessage',function(o){return function(){
     try{s.clear(this);}catch(e){s.fail('clear',e);}
     var r=o.apply(this,arguments);
     try{var f=s.friends.get(this);if(f)s.one(this,f.accountid,f);}catch(e){s.fail('update',e);}
     s.later();return r;};});
    s.patch(proto,'GetCurrentGameIconURL',function(o){return function(){try{var i=s.icon(this);if(i)return i;}catch(e){s.fail('icon',e);}return o.apply(this,arguments);};});
   }
   s.one(p,f.accountid,f);
  });
 };
 s.later=function(){if(s.pending)return;try{s.pending=setTimeout(function(){s.pending=0;s.apply();},250);}catch(e){s.pending=0;}};
 s.dispose=function(){
 s.labels={};s.written.forEach(function(v,p){s.clear(p);});
 clearInterval(s.timer);if(s.pending)clearTimeout(s.pending);
 s.patches.forEach(function(v,p){Object.keys(v).forEach(function(n){if(p[n]===v[n].wrapped)p[n]=v[n].original;});});
 if(s.unpatchGroups)s.unpatchGroups();
 var w=s.spacewarInfo;if(w&&w.copy&&w.map.get(480)===w.copy)w.map.set(480,w.original);
 if(window.__cabinetLabels===s)delete window.__cabinetLabels;
};
s.apply=function(){s.step('labels',s.labelAll);if(!Object.keys(s.labels).length){s.step('dispose',s.dispose);return;}
 s.step('spacewar',s.patchSpacewar);s.step('groups',s.patchGroups);s.step('nudge',s.nudge);};
 s.timer=setInterval(s.apply,5000);
}
s.fallback=fallback;s.labels=labels;s.expires=Date.now()+180000;s.apply();return JSON.stringify(s.errors);
})(" + System.Text.Json.JsonSerializer.Serialize(labels) + "," + "\"\"" + ")";
        }
}
