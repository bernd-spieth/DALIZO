'use strict';
const post=m=>window.chrome.webview.postMessage(m),enc=new TextEncoder();
let term,fit,sharing=false,readOnly=true,connected=false,auth=[],resizeEnabled=true;
const menu=document.getElementById('menu');
function encode(b){let s='';for(let i=0;i<b.length;i+=8192)s+=String.fromCharCode(...b.subarray(i,i+8192));return btoa(s);}
function send(b){post({type:'send',data:encode(typeof b==='string'?enc.encode(b):b)});}
function input(text){if(!connected||readOnly)return;const b=enc.encode(text);for(let i=0;i<b.length;i+=16384){const chunk=b.subarray(i,i+16384),header=enc.encode('0:'+chunk.length+':');const packet=new Uint8Array(header.length+chunk.length);packet.set(header);packet.set(chunk,header.length);send(packet);}}
function resize(){if(term&&document.body.clientWidth>40&&document.body.clientHeight>40)fit.fit();}
function copy(){if(sharing&&term?.hasSelection())post({type:'clipboard',text:term.getSelection()});menu.hidden=true;}
function paste(){menu.hidden=true;term?.focus();if(sharing&&!readOnly&&connected)post({type:'paste-request'});}
document.getElementById('copy').onclick=copy;document.getElementById('paste').onclick=paste;
document.addEventListener('pointerdown',e=>{if(!menu.contains(e.target))menu.hidden=true;});
document.addEventListener('contextmenu',e=>{e.preventDefault();if(!term)return;menu.hidden=false;menu.style.left=Math.max(0,Math.min(e.clientX,innerWidth-234))+'px';menu.style.top=Math.max(0,Math.min(e.clientY,innerHeight-menu.offsetHeight))+'px';});
window.chrome.webview.addEventListener('message',e=>{const m=e.data;try{switch(m.type){
case 'start':
 term?.dispose();document.getElementById('terminal').replaceChildren();auth=[];connected=false;readOnly=m.readOnly;sharing=m.sharing&&!readOnly;resizeEnabled=m.resize;
 term=new Terminal({fontFamily:'Consolas, monospace',fontSize:14,scrollback:10000,convertEol:false,allowProposedApi:false,theme:{background:'#101820',foreground:'#e6edf3'},disableStdin:readOnly});fit=new FitAddon.FitAddon();term.loadAddon(fit);term.open(document.getElementById('terminal'));resize();
 term.onData(input);term.onResize(s=>{if(connected&&resizeEnabled)send('1:'+s.cols+':'+s.rows+':');});
 term.attachCustomKeyEventHandler(ev=>{if(ev.type==='keydown'&&ev.ctrlKey&&ev.shiftKey&&ev.code==='KeyC'){ev.preventDefault();copy();return false;}if(ev.type==='keydown'&&ev.ctrlKey&&ev.shiftKey&&ev.code==='KeyV'){ev.preventDefault();paste();return false;}return true;});
 // Block the browser's implicit paste so the host sharing setting is authoritative.
 term.element.addEventListener('paste',ev=>{ev.preventDefault();ev.stopImmediatePropagation();paste();},true);
 send(m.user+':'+m.password+'\n');post({type:'started'});break;
case 'data':{
 let b=Uint8Array.from(atob(m.data),c=>c.charCodeAt(0));
 if(!connected){const all=new Uint8Array(auth.length+b.length);all.set(auth);all.set(b,auth.length);if(all.length<2){auth=all;post({type:'ack'});break;}if(all[0]!==79||all[1]!==75){post({type:'authfailed'});post({type:'ack'});break;}auth=[];connected=true;b=all.subarray(2);post({type:'connected'});if(resizeEnabled)send('1:'+term.cols+':'+term.rows+':');term.focus();}
 term.write(b,()=>post({type:'ack'}));break;}
case 'paste':if(sharing&&!readOnly&&connected){term.paste(m.text);term.focus();}break;
case 'sharing':sharing=m.enabled&&!readOnly;break;
case 'resize':resizeEnabled=!!m.enabled;resize();if(connected&&resizeEnabled)send('1:'+term.cols+':'+term.rows+':');break;
case 'focus':term?.focus();break;
case 'blur':term?.blur();menu.hidden=true;break;
case 'disconnect':connected=false;sharing=false;term?.dispose();term=null;menu.hidden=true;break;
case 'serial-test':if(m.operation==='input')input(m.text);if(m.operation==='select')term.select(0,0,m.length);if(m.operation==='copy')copy();if(m.operation==='text'){let lines=[];for(let i=0;i<term.buffer.active.length;i++)lines.push(term.buffer.active.getLine(i).translateToString(true));post({type:'serial-text',text:lines.join('\n')});}break;
}}catch{post({type:'error'});}});
new ResizeObserver(resize).observe(document.getElementById('terminal'));post({type:'ready'});


