import RFB from './core/rfb.js';
let rfb, channel, sharing=false, readOnly=true;
const post = data => window.chrome.webview.postMessage(data);
function encode(data) {
    const bytes = data instanceof Uint8Array ? data : new Uint8Array(data);
    let s=''; for (let i=0;i<bytes.length;i+=8192) s+=String.fromCharCode(...bytes.subarray(i,i+8192));
    return btoa(s);
}
class HostChannel {
    constructor() {
        this.readyState=1; this.protocol='binary'; this.binaryType='arraybuffer';
        this.onopen=null; this.onmessage=null; this.onerror=null; this.onclose=null;
    }
    send(data) { if(this.readyState===1) post({type:'send',data:encode(data)}); }
    close() { if(this.readyState===3)return; this.readyState=3; post({type:'close'}); }
}
window.chrome.webview.addEventListener('message', e => {
    const m=e.data;
    try {
        switch(m.type) {
        case 'start':
            rfb?.disconnect(); document.getElementById('screen').replaceChildren();
            channel=new HostChannel(); readOnly=m.readOnly; sharing=m.sharing && !readOnly;
            rfb=new RFB(document.getElementById('screen'),channel,{credentials:{username:m.user,password:m.password},shared:true});
            rfb.scaleViewport=true; rfb.viewOnly=readOnly; rfb.resizeSession=!!m.resize && !readOnly;
            rfb.addEventListener('connect',()=>post({type:'connected'}));
            rfb.addEventListener('disconnect',()=>post({type:'disconnected'}));
            rfb.addEventListener('securityfailure',()=>post({type:'authfailed'}));
            rfb.addEventListener('credentialsrequired',()=>post({type:'authfailed'}));
            rfb.addEventListener('clipboard',ev=>{if(sharing && ev.detail.text.length<=1048576)post({type:'clipboard',text:ev.detail.text});});
            post({type:'started'}); break;
        case 'data': {
            const bytes=Uint8Array.from(atob(m.data),c=>c.charCodeAt(0));
            channel?.onmessage?.({data:bytes.buffer}); post({type:'ack'}); break;
        }
        case 'disconnect':
            sharing=false; rfb?.disconnect(); rfb=null; channel=null; document.getElementById('screen').replaceChildren(); break;
        case 'focus': rfb?.focus(); break;
        case 'blur': rfb?.blur(); break;
        case 'cad': if(!readOnly)rfb?.sendCtrlAltDel(); break;
        case 'keys': {
            if(readOnly || !rfb || !Array.isArray(m.keys) || m.keys.length<1 || m.keys.length>8)break;
            if(!m.keys.every(k=>Number.isInteger(k.keysym) && typeof k.code==='string'))break;
            const sent=[];
            try { for(const k of m.keys){rfb.sendKey(k.keysym,k.code,true);sent.push(k);} }
            finally { for(const k of sent.reverse())rfb.sendKey(k.keysym,k.code,false); }
            break;
        }
        case 'resize': if(rfb)rfb.resizeSession=!!m.enabled && !readOnly; break;
        case 'sharing': sharing=m.enabled && !readOnly; break;
        case 'paste': if(sharing)rfb?.clipboardPasteFrom(m.text); break;
        case 'wake': {
            if(!m.diagnostic)break;
            rfb.viewOnly=false;
            const c=document.querySelector('canvas'); const r=c.getBoundingClientRect();
            c.dispatchEvent(new MouseEvent('mousemove',{bubbles:true,clientX:r.left+r.width/2+1,clientY:r.top+r.height/2}));
            setTimeout(()=>{rfb.viewOnly=readOnly; post({type:'woke'});},150); break;
        }
        case 'shift-test':
            if(m.diagnostic) { rfb.viewOnly=false; rfb.sendKey(0xffe1,'ShiftLeft',true); rfb.sendKey(0xffe1,'ShiftLeft',false); rfb.viewOnly=readOnly; } break;
        case 'stats': {
            const im=rfb?.getImageData(); let nonblack=0;
            if(im)for(let i=0;i<im.data.length;i+=4)if(im.data[i]||im.data[i+1]||im.data[i+2])nonblack++;
            post({type:'stats',width:im?.width||0,height:im?.height||0,nonblack,queued:rfb?._display?._renderQ?.length||0,rects:rfb?._FBU?.rects||0,encoding:rfb?._FBU?.encoding||0}); break;
        }
        }
    } catch { post({type:'error'}); }
});
document.addEventListener('keydown', e=>{
    if(e.ctrlKey && e.altKey && (e.code==='AltLeft'||e.code==='ControlLeft')) {
        e.preventDefault(); e.stopImmediatePropagation(); rfb?.blur(); post({type:'release'});
    }
},true);
window.addEventListener('error',()=>post({type:'error'}));
window.addEventListener('unhandledrejection',()=>post({type:'error'}));
post({type:'ready'});


