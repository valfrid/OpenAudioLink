#pragma once

/*
 * The page a node serves at "/".
 *
 * Why a node needs one at all: until now the control server answered
 * /status, /volume, /peers and the rest, and nothing at the root. The only
 * HTML in the firmware was the provisioning portal, which is a separate
 * server that exists only when the node has no network. So with the Hub
 * off — or absent entirely, which is the whole point of decision 4's
 * standalone mode — there was no way to set a speaker's volume by hand.
 *
 * Every node serves this, not just a producer, and that is what keeps it
 * small. A producer page carrying sliders for its consumers would be
 * posting across origins, which needs CORS headers and OPTIONS preflight
 * on every node, or a forwarding endpoint on the producer. Instead each
 * node adjusts only itself and the producer *links* to the others: every
 * request is same-origin and nothing new is needed on the wire.
 *
 * Two rules this file must keep:
 *
 * **Nothing external.** No CDN, no web font, no icon set. On an island
 * there is no internet, so anything not in this flash simply does not
 * arrive. Same rule the portal page already follows.
 *
 * **No double-quote characters.** The whole page is one C string literal;
 * single quotes throughout mean it needs no escaping, which is the
 * difference between editing HTML and editing HTML-inside-C. Values are
 * written with textContent rather than innerHTML, so nothing needs
 * escaping at runtime either.
 */
static const char NODE_PAGE[] =
"<!doctype html>\n"
"<html lang='en'><head>\n"
"<meta charset='utf-8'>\n"
"<meta name='viewport' content='width=device-width,initial-scale=1'>\n"
"<title>OpenAudioLink</title>\n"
"<style>\n"
":root{color-scheme:light dark}\n"
"body{font-family:system-ui,sans-serif;margin:0 auto;max-width:30rem;\n"
"padding:1.25rem;line-height:1.5}\n"
"h1{font-size:1.35rem;margin:0}\n"
"h2{font-size:.8rem;text-transform:uppercase;letter-spacing:.05em;\n"
"margin:1.75rem 0 .4rem;color:#7a8494}\n"
".muted{color:#7a8494;font-size:.85rem;margin:.25rem 0}\n"
".warn{color:#c94f4f}\n"
"#vol{font-size:2.5rem;font-variant-numeric:tabular-nums;line-height:1}\n"
"input[type=range]{width:100%;margin:.5rem 0 0}\n"
"button{font:inherit;padding:.5rem 1.1rem;margin:0 .5rem .5rem 0}\n"
"ul{list-style:none;padding:0;margin:0}\n"
"li{padding:.6rem 0;border-bottom:1px solid #8884}\n"
"a{color:#2b7de9;text-decoration:none}\n"
"</style></head><body>\n"
"<h1 id='name'>&hellip;</h1>\n"
"<p id='sub' class='muted'></p>\n"
"<p id='ready' class='muted warn' hidden></p>\n"

"<h2>Volume</h2>\n"
"<div id='vol'>&mdash;</div>\n"
"<input id='slider' type='range' min='0' max='100' step='1' value='0'>\n"

"<section id='streambox' hidden>\n"
"<h2>Stream</h2>\n"
"<button id='start'>Start line in</button>\n"
"<button id='stop'>Stop</button>\n"
"<p id='streamnote' class='muted'></p>\n"
"</section>\n"

"<section id='peerbox' hidden>\n"
"<h2>Other nodes</h2>\n"
"<ul id='peers'></ul>\n"
"</section>\n"

"<script>\n"
"const $=i=>document.getElementById(i);\n"
"let dragging=false,timer=null,isProducer=false;\n"

/* The slider is the one control that must feel immediate. The readout
 * follows the thumb, the POST is debounced, and `dragging` stops the
 * refresh from yanking the thumb back to a value the node has not been
 * told about yet. */
"function send(v){\n"
" fetch('/volume',{method:'POST',\n"
"  headers:{'Content-Type':'application/json'},\n"
"  body:JSON.stringify({percent:v})}).catch(()=>{});\n"
"}\n"
"$('slider').addEventListener('input',e=>{\n"
" dragging=true;$('vol').textContent=e.target.value+'%';\n"
" clearTimeout(timer);\n"
" timer=setTimeout(()=>{send(+e.target.value);dragging=false;},150);\n"
"});\n"

"async function load(){\n"
" let s;\n"
" try{s=await (await fetch('/status',{cache:'no-store'})).json();}\n"
" catch(e){$('sub').textContent='Not answering.';return;}\n"
" $('name').textContent=s.name||s.id||'node';\n"
" const roles=s.roles||[];\n"
" $('sub').textContent=roles.join(', ')+' \\u00b7 '+(s.output||'i2s')\n"
"  +' \\u00b7 '+(s.fw||'');\n"

/* The field that separates no sound from no audio. A USB node with no
 * dongle plugged in is online, joined and streaming, with every counter
 * rising and silence in the room; this is the only thing that says so,
 * and on an island there is no Hub to say it instead. */
" const bad=s.outputReady===false;\n"
" $('ready').hidden=!bad;\n"
" if(bad){$('ready').textContent='Output not ready \\u2014 nothing will be heard'\n"
"  +(s.outputArrivedAs?' ('+s.outputArrivedAs+')':'');}\n"

" if(!dragging){$('slider').value=s.volume??0;\n"
"  $('vol').textContent=(s.volume??0)+'%';}\n"
" isProducer=roles.indexOf('producer')>=0;\n"
" $('streambox').hidden=!isProducer;\n"
" if(isProducer)stream();\n"
" peers();\n"
"}\n"

/* The producer's /stream is flat -- role, running, destinationList at the
 * top level -- and `destinations` there is a *count*, not the list. The
 * list is `destinationList`, which exists precisely so a stream pointed at
 * an address a speaker no longer has can be told apart from a healthy one;
 * a count looks identical either way. */
"async function stream(){\n"
" try{const t=await (await fetch('/stream',{cache:'no-store'})).json();\n"
"  $('streamnote').textContent=t.running\n"
"   ?'Sending to '+((t.destinationList||[]).join(', ')||'nobody')\n"
"   :'Stopped.';\n"
" }catch(e){$('streamnote').textContent='';}\n"
"}\n"

/* Every peer is a link to its own page, which is what makes this work
 * without cross-origin anything: you adjust a speaker by opening the
 * speaker. */
"async function peers(){\n"
" let d;\n"
" try{d=await (await fetch('/peers',{cache:'no-store'})).json();}catch(e){return;}\n"
" const list=d.peers||[];\n"
" $('peerbox').hidden=list.length===0;\n"
" const ul=$('peers');ul.textContent='';\n"
" for(const p of list){\n"
"  const li=document.createElement('li');\n"
"  const a=document.createElement('a');\n"
"  a.href='http://'+p.address+':'+(p.ctrlPort||41001)+'/';\n"
"  a.textContent=p.name||p.id;\n"
"  const t=document.createElement('span');\n"
"  t.className='muted';\n"
"  t.textContent=' \\u00b7 '+(p.roles||[]).join(', ')+' \\u00b7 '+p.address;\n"
"  li.appendChild(a);li.appendChild(t);ul.appendChild(li);\n"
" }\n"
"}\n"

/* Line in, because that is what a producer on an island is for: a
 * turntable and a speaker with nothing else present. Everything else the
 * producer can send is a test signal and belongs on the Hub's page. */
"$('start').addEventListener('click',async()=>{\n"
" let d;\n"
" try{d=await (await fetch('/peers',{cache:'no-store'})).json();}catch(e){return;}\n"
" const to=(d.peers||[]).filter(p=>(p.roles||[]).indexOf('consumer')>=0)\n"
"  .map(p=>p.address);\n"
" if(!to.length){$('streamnote').textContent='No consumer has been heard yet.';return;}\n"
" $('streamnote').textContent='Starting\\u2026';\n"
" await fetch('/stream/start',{method:'POST',\n"
"  headers:{'Content-Type':'application/json'},\n"
"  body:JSON.stringify({destinations:to,source:'capture'})}).catch(()=>{});\n"
" stream();\n"
"});\n"
"$('stop').addEventListener('click',async()=>{\n"
" await fetch('/stream/stop',{method:'POST'}).catch(()=>{});\n"
" stream();\n"
"});\n"

"load();setInterval(load,3000);\n"
"</script></body></html>\n";
