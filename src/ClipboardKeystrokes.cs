// SPDX-License-Identifier: GPL-3.0-only
namespace ProxmoxSpice;
internal static class ClipboardKeystrokes
{
    public static List<GuestKey[]> Build(string text,bool german,bool allowControls)
    {
        if(text.Length>4096)throw new InvalidOperationException("Use at most 4096 characters per typing operation.");
        var map=new Dictionary<char,GuestKey[]>();
        var shift=new GuestKey(0x2a,0xffe1,"ShiftLeft");var altgr=new GuestKey(0x138,0xffea,"AltRight");
        void Add(char c,uint scan,string code,GuestKey? modifier=null,bool dead=false){var key=new GuestKey(scan,c,code);map[c]=modifier==null?new[]{key}:new[]{modifier,key};}
        void Pair(char low,char high,uint scan,string code){Add(low,scan,code);Add(high,scan,code,shift);}
        uint[] letters={0x1e,0x30,0x2e,0x20,0x12,0x21,0x22,0x23,0x17,0x24,0x25,0x26,0x32,0x31,0x18,0x19,0x10,0x13,0x1f,0x14,0x16,0x2f,0x11,0x2d,0x15,0x2c};
        for(int i=0;i<26;i++){char c=(char)('a'+i);char physical=german&&c=='y'?'z':german&&c=='z'?'y':c;Pair(c,char.ToUpperInvariant(c),letters[physical-'a'],"Key"+char.ToUpperInvariant(physical));}
        string digits="1234567890",tops=german?"!\"§$%&/()=":"!@#$%^&*()";
        for(int i=0;i<10;i++)Pair(digits[i],tops[i],(uint)(i+2),"Digit"+digits[i]);
        if(german){
            Pair('^','°',0x29,"Backquote");Pair('ß','?',0x0c,"Minus");Pair('´','`',0x0d,"Equal");Pair('ü','Ü',0x1a,"BracketLeft");Pair('+','*',0x1b,"BracketRight");Pair('#','\'',0x2b,"Backslash");Pair('ö','Ö',0x27,"Semicolon");Pair('ä','Ä',0x28,"Quote");Pair(',',';',0x33,"Comma");Pair('.',':',0x34,"Period");Pair('-','_',0x35,"Slash");Pair('<','>',0x56,"IntlBackslash");
            Add('@',0x10,"KeyQ",altgr);Add('€',0x12,"KeyE",altgr);Add('{',8,"Digit7",altgr);Add('[',9,"Digit8",altgr);Add(']',10,"Digit9",altgr);Add('}',11,"Digit0",altgr);Add('\\',0x0c,"Minus",altgr);Add('|',0x56,"IntlBackslash",altgr);Add('~',0x1b,"BracketRight",altgr);
        } else {
            Pair('`','~',0x29,"Backquote");Pair('-','_',0x0c,"Minus");Pair('=','+',0x0d,"Equal");Pair('[','{',0x1a,"BracketLeft");Pair(']','}',0x1b,"BracketRight");Pair('\\','|',0x2b,"Backslash");Pair(';',':',0x27,"Semicolon");Pair('\'','"',0x28,"Quote");Pair(',','<',0x33,"Comma");Pair('.','>',0x34,"Period");Pair('/','?',0x35,"Slash");
        }
        Add(' ',0x39,"Space");map['\n']=new[]{new GuestKey(0x1c,0xff0d,"Enter")};map['\t']=new[]{new GuestKey(0x0f,0xff09,"Tab")};
        var result=new List<GuestKey[]>();int position=0;
        foreach(char c in text.Replace("\r\n","\n").Replace('\r','\n')){
            position++;
            if((c=='\n'||c=='\t')&&!allowControls)throw new InvalidOperationException("The text contains line breaks or tabs. Enable Enter/Tab explicitly or remove them before sending.");
            if(!map.TryGetValue(c,out var chord))throw new InvalidOperationException($"Character at position {position} is not supported by this keyboard layout. Nothing was sent.");
            result.Add(chord);if(german && c is '^' or '´' or '`')result.Add(map[' ']);
        }
        return result;
    }
}
