/* Downloads original small icons from public miHoYo-hosted catalogs; no account APIs or credentials. */
const https = require('https');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const root = path.resolve(__dirname, '..');
const assetRoot = path.join(root, 'src', 'AsterLauncher.App', 'Assets', 'Games', 'Gacha', 'Portraits');
const indexPath = path.join(assetRoot, 'portrait-index.json');
const allowed = new Set(['api-static.mihoyo.com','act-upload.mihoyo.com','upload-bbs.mihoyo.com','webstatic.mihoyo.com','fastcdn.mihoyo.com','uploadstatic.mihoyo.com']);
const specs = [
 {game:'genshin-impact',sn:'ys_obc',channel:189,folder:'GenshinImpact',groups:['角色','武器'],page:'https://bbs.mihoyo.com/ys/obc/content/'},
 {game:'honkai-star-rail',sn:'sr_wiki',channel:17,folder:'HonkaiStarRailWiki',groups:['角色','光锥'],page:'https://bbs.mihoyo.com/sr/wiki/content/'},
 {game:'zenless-zone-zero',sn:'zzz_wiki',channel:2,folder:'ZenlessZoneZero',groups:['代理人','音擎','邦布'],page:'https://bbs.mihoyo.com/zzz/wiki/content/'}
];
function fetchBytes(url, redirects=0) {
 const uri = new URL(url);
 if(uri.protocol!=='https:' || uri.username || uri.password || !allowed.has(uri.hostname)) throw new Error('Unapproved media host: '+uri.hostname);
 return new Promise((resolve,reject)=>{
  const request=https.get(uri,{headers:{'User-Agent':'AsterLauncher asset catalog/1.0'}},response=>{
   if([301,302,303,307,308].includes(response.statusCode)&&redirects<3&&response.headers.location){
    response.resume();fetchBytes(new URL(response.headers.location,uri).href,redirects+1).then(resolve,reject);return;
   }
   if(response.statusCode!==200){response.resume();reject(new Error('HTTP '+response.statusCode));return;}
   const buffers=[];let size=0;
   response.on('data',buffer=>{size+=buffer.length;if(size>8*1024*1024){request.destroy(new Error('Oversized icon'));return;}buffers.push(buffer);});
   response.on('end',()=>resolve(Buffer.concat(buffers)));
   response.on('error',reject);
  });
  request.setTimeout(25000,()=>request.destroy(new Error('Timeout')));request.on('error',reject);
 });
}
function isImage(b){return b.length>128 && (
 b.subarray(0,8).equals(Buffer.from([137,80,78,71,13,10,26,10])) ||
 (b[0]===255&&b[1]===216&&b[2]===255) ||
 (b.toString('ascii',0,4)==='RIFF'&&b.toString('ascii',8,12)==='WEBP'));}
(async()=>{
 const existing=JSON.parse(fs.readFileSync(indexPath,'utf8').replace(/^\uFEFF/,''));
 const map=new Map();
 for(const entry of existing){const key=entry.GameId+'\x1f'+entry.Name;if(!map.has(key))map.set(key,entry);}
 let completed=0,bytes=0;
 for(const spec of specs){
  const catalogUrl='https://api-static.mihoyo.com/common/blackboard/'+spec.sn+'/v1/home/content/list?app_sn='+spec.sn+'&channel_id='+spec.channel;
  const data=JSON.parse((await fetchBytes(catalogUrl)).toString('utf8'));
  if(data.retcode!==0 || !data.data?.list?.length) throw new Error('Invalid catalog '+spec.sn);
  const groups=data.data.list.flatMap(node=>node.children||[]).filter(node=>spec.groups.includes(node.name));
  if(groups.length!==spec.groups.length)throw new Error('Incomplete catalog groups');
  const jobs=groups.flatMap(group=>(group.list||[]).filter(item=>item.icon).map(item=>({item,kind:group.name})));
  let next=0;
  async function worker(){
   for(;;){
    const position=next++;if(position>=jobs.length)return;
    const {item,kind}=jobs[position];
    if(!/^\d+$/.test(String(item.content_id)))throw new Error('Unsafe catalog item id');
    const names=[item.title,...String(item.alias_name||'').split(/[,，]/)].map(name=>name.trim()).filter(Boolean)
     .flatMap(name=>/[\u3400-\u9fff]\s+[\u3400-\u9fff]/.test(name) ? [name,name.replace(/\s+/g,'')] : [name]);
    if(names.every(name=>{const prior=map.get(spec.game+'\x1f'+name);return prior&&!prior.CatalogUrl;}))continue;
    const extension=path.extname(new URL(item.icon).pathname).toLowerCase();
    if(!['.png','.jpg','.jpeg','.webp'].includes(extension))throw new Error('Unknown icon type');
    const relative=spec.folder+'/'+item.content_id+extension;
    const full=path.join(assetRoot,relative);fs.mkdirSync(path.dirname(full),{recursive:true});
    let buffer;
    const prior=existing.find(entry=>entry.GameId===spec.game&&entry.File===relative&&entry.SourceUrl===item.icon);
    if(fs.existsSync(full)&&prior?.Sha256){
     const cached=fs.readFileSync(full);
     if(crypto.createHash('sha256').update(cached).digest('hex')===prior.Sha256)buffer=cached;
    }
    if(!buffer){
     for(let attempt=0;attempt<3;attempt++){try{buffer=await fetchBytes(item.icon);break;}catch(e){if(attempt===2)throw e;await new Promise(r=>setTimeout(r,500*(attempt+1)));}}
     if(!isImage(buffer))throw new Error('Invalid icon '+item.content_id);
     fs.writeFileSync(full+'.download',buffer);fs.renameSync(full+'.download',full);
    }
    if(!isImage(buffer))throw new Error('Cached icon is corrupt');
    const entry={GameId:spec.game,Name:item.title,CatalogName:item.title,Kind:kind,File:relative,SourceUrl:item.icon,
     SourcePage:spec.page+item.content_id+'/detail',CatalogUrl:catalogUrl,
     Sha256:crypto.createHash('sha256').update(buffer).digest('hex'),SizeBytes:buffer.length};
    // Existing official character-site portraits keep precedence. New aliases still resolve to a bundled file.
    for(const name of names){const key=spec.game+'\x1f'+name;if(!map.has(key)||map.get(key).CatalogUrl===catalogUrl)map.set(key,{...entry,Name:name});}
    completed++;bytes+=buffer.length;
    if(completed%50===0)console.log('Validated '+completed+' icons');
   }
  }
  await Promise.all(Array.from({length:4},worker));
  console.log(spec.game+': '+jobs.length+' catalog icons');
 }
 const entries=[...map.values()].sort((a,b)=>(a.GameId+'\x1f'+a.Name).localeCompare(b.GameId+'\x1f'+b.Name,'zh-CN'));
 fs.writeFileSync(indexPath+'.new',JSON.stringify(entries,null,2)+'\n');fs.renameSync(indexPath+'.new',indexPath);
 const referenced=new Set(entries.map(entry=>entry.File));
 for(const spec of specs){
  const folder=path.join(assetRoot,spec.folder);
  for(const filename of fs.readdirSync(folder)){
   const relative=spec.folder+'/'+filename;
   if(/^\d+\.(png|jpg|jpeg|webp)$/.test(filename)&&!referenced.has(relative))fs.unlinkSync(path.join(folder,filename));
  }
 }
 console.log('Catalog complete: '+completed+' original icons, '+bytes+' bytes; '+entries.length+' name mappings.');
})().catch(e=>{console.error('Media import failed: '+e.message);process.exitCode=1;});