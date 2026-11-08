using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ncp
{
    internal interface IDisplayIccApi
    {
        int Scope(DisplayPath path);
        string DefaultName(DisplayPath path,int scope,int subtype);
        List<string> Names(DisplayPath path,int scope);
        void Associate(DisplayPath path,string name,bool setDefault,bool advanced);
        void SetDefault(DisplayPath path,string name,int subtype);
        void Remove(DisplayPath path,string name,bool advanced);
    }
    // Use one display-specific API family for association, selection and readback.
    // In particular, never assume that an association alone proves the default.
    internal sealed class DisplayIccAssociations:IIccAssociations
    {
        const int CurrentUser=1,IccType=0,Standard=7;
        readonly IIccAssociations legacy;
        readonly IDisplayIccApi api;
        readonly Func<DisplayTarget,DisplayPath> resolve;
        internal DisplayIccAssociations(IIccAssociations legacy,IDisplayIccApi api=null,Func<DisplayTarget,DisplayPath> resolve=null)
        {this.legacy=legacy;this.api=api??new NativeApi();this.resolve=resolve??new Func<DisplayTarget,DisplayPath>(WindowsDisplayPath.Resolve);}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("mscms.dll",ExactSpelling=true)] static extern int ColorProfileGetDisplayUserScope(AdapterLuid adapter,uint source,out int scope);
        [DllImport("mscms.dll",ExactSpelling=true)] static extern int ColorProfileGetDisplayDefault(int scope,AdapterLuid adapter,uint source,int type,int subtype,out IntPtr name);
        [DllImport("mscms.dll",ExactSpelling=true)] static extern int ColorProfileGetDisplayList(int scope,AdapterLuid adapter,uint source,out IntPtr names,out uint count);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern int ColorProfileAddDisplayAssociation(int scope,string name,AdapterLuid adapter,uint source,bool setDefault,bool advanced);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern int ColorProfileSetDisplayDefaultAssociation(int scope,string name,int type,int subtype,AdapterLuid adapter,uint source);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern int ColorProfileRemoveDisplayAssociation(int scope,string name,AdapterLuid adapter,uint source,bool advanced);
        internal static readonly bool Available=Environment.OSVersion.Version.Build>=20348 && HasFunctions();
        static bool HasFunctions()
        {
            IntPtr library=LoadLibraryEx(Path.Combine(Environment.SystemDirectory,"mscms.dll"),IntPtr.Zero,0x800);
            if(library==IntPtr.Zero)return false;
            try {return new[]{"ColorProfileGetDisplayUserScope","ColorProfileGetDisplayDefault","ColorProfileGetDisplayList",
                "ColorProfileAddDisplayAssociation","ColorProfileSetDisplayDefaultAssociation","ColorProfileRemoveDisplayAssociation"}
                .All(n=>GetProcAddress(library,n)!=IntPtr.Zero);}
            finally {FreeLibrary(library);}
        }
        static void Check(int result,string action)
        {
            if(result<0)throw new COMException(action+"에 실패했습니다. (Windows HRESULT 0x"+((uint)result).ToString("X8")+")",result);
        }
        static bool Missing(int result) {return result==unchecked((int)0x80070002) || result==unchecked((int)0x80070490);}
        static int Scope(DisplayPath path)
        {
            int scope;Check(ColorProfileGetDisplayUserScope(path.Adapter,path.SourceId,out scope),"디스플레이 ICC 사용자 범위 조회");
            if(scope!=0 && scope!=1)throw new InvalidDataException("디스플레이 ICC 사용자 범위가 올바르지 않습니다.");return scope;
        }
        static string DefaultName(DisplayPath path,int scope,int subtype)
        {
            IntPtr name=IntPtr.Zero;
            try {
                int result=ColorProfileGetDisplayDefault(scope,path.Adapter,path.SourceId,IccType,subtype,out name);
                if(Missing(result))return null;
                Check(result,"디스플레이 기본 ICC 조회");
                string text=name==IntPtr.Zero?null:Marshal.PtrToStringUni(name);
                return string.IsNullOrWhiteSpace(text)?null:Path.GetFileName(text);
            } finally {if(name!=IntPtr.Zero)LocalFree(name);}
        }
        static List<string> Names(DisplayPath path,int scope)
        {
            IntPtr names=IntPtr.Zero;
            try {
                uint count;int result=ColorProfileGetDisplayList(scope,path.Adapter,path.SourceId,out names,out count);
                if(Missing(result))return new List<string>();
                Check(result,"디스플레이 ICC 연결 목록 조회");
                if(count>2048 || (count>0 && names==IntPtr.Zero))throw new InvalidDataException("디스플레이 ICC 목록이 올바르지 않습니다.");
                var list=new List<string>();
                for(int i=0;i<count;i++) {
                    IntPtr item=Marshal.ReadIntPtr(names,i*IntPtr.Size);
                    if(item==IntPtr.Zero)throw new InvalidDataException("디스플레이 ICC 이름이 없습니다.");
                    string name=Marshal.PtrToStringUni(item);
                    if(string.IsNullOrWhiteSpace(name))throw new InvalidDataException("디스플레이 ICC 이름이 없습니다.");
                    list.Add(Path.GetFileName(name));
                }
                return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            } finally {if(names!=IntPtr.Zero)LocalFree(names);}
        }
        sealed class NativeApi:IDisplayIccApi
        {
            public int Scope(DisplayPath path) {return DisplayIccAssociations.Scope(path);}
            public string DefaultName(DisplayPath path,int scope,int subtype) {return DisplayIccAssociations.DefaultName(path,scope,subtype);}
            public List<string> Names(DisplayPath path,int scope) {return DisplayIccAssociations.Names(path,scope);}
            public void Associate(DisplayPath path,string name,bool setDefault,bool advanced)
            {Check(ColorProfileAddDisplayAssociation(CurrentUser,name,path.Adapter,path.SourceId,setDefault,advanced),"ICC 디스플레이 연결");}
            public void SetDefault(DisplayPath path,string name,int subtype)
            {Check(ColorProfileSetDisplayDefaultAssociation(CurrentUser,name,IccType,subtype,path.Adapter,path.SourceId),"디스플레이 기본 ICC 적용");}
            public void Remove(DisplayPath path,string name,bool advanced)
            {Check(ColorProfileRemoveDisplayAssociation(CurrentUser,name,path.Adapter,path.SourceId,advanced),"이전 NCP ICC 디스플레이 연결 해제");}
        }
        public IccState Read(DisplayTarget display)
        {
            var path=resolve(display);int scope=api.Scope(path);
            if(scope!=0 && scope!=1)throw new InvalidDataException("디스플레이 ICC 사용자 범위가 올바르지 않습니다.");
            var state=new IccState {PerUser=scope==CurrentUser,ProfileName=api.DefaultName(path,scope,Standard),Profiles=api.Names(path,CurrentUser)};
            state.Validate();return state;
        }
        public void UsePerUser(DisplayTarget display,bool enabled)
        {
            legacy.UsePerUser(display,enabled);
            // The stored checkbox and the display API must refer to the same scope.
            // If only the stored flag changed, ask WCS to initialize the actual scope.
            if((api.Scope(resolve(display))==CurrentUser)!=enabled)
                WindowsIccBackend.SetPerUserNative(display,enabled);
        }
        public void Associate(DisplayTarget display,string name)
        {
            api.Associate(resolve(display),name,false,false);
        }
        public void SetDefault(DisplayTarget display,string name)
        {
            api.SetDefault(resolve(display),name,Standard);
        }
        public void Remove(DisplayTarget display,string name)
        {
            api.Remove(resolve(display),name,false);
        }
        internal static string Describe(DisplayTarget display)
        {
            if(!Available)return "Display ColorProfile APIs: unavailable (legacy WCS)";
            var path=WindowsDisplayPath.Resolve(display);
            var log=new System.Text.StringBuilder();
            log.AppendLine("Display adapter: "+path.Adapter+", source: "+path.SourceId+", target: "+path.TargetId);
            log.AppendLine("Display color mode: "+WindowsDisplayPath.ColorMode(display));
            log.AppendLine("Display user scope: "+Scope(path));
            foreach(int scope in new[]{0,1}) {
                log.AppendLine("Display scope "+scope+" standard default: "+(DefaultName(path,scope,Standard)??"none"));
                log.AppendLine("Display scope "+scope+" HDR default: "+(DefaultName(path,scope,8)??"none"));
                log.AppendLine("Display scope "+scope+" profiles: "+string.Join(", ",Names(path,scope)));
            }
            return log.ToString();
        }
    }
}
