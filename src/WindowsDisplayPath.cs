using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ncp
{
    [StructLayout(LayoutKind.Sequential)] internal struct AdapterLuid
    {
        internal uint Low;internal int High;
        public override string ToString() {return ((uint)High).ToString("X8")+Low.ToString("X8");}
    }
    internal sealed class DisplayPath
    {
        internal string GdiName,InterfaceId;
        internal AdapterLuid Adapter;
        internal uint SourceId,TargetId;
    }
    internal static class WindowsDisplayPath
    {
        [StructLayout(LayoutKind.Sequential)] struct SourceInfo {internal AdapterLuid Adapter;internal uint Id,ModeIndex,Flags;}
        [StructLayout(LayoutKind.Sequential)] struct TargetInfo {
            internal AdapterLuid Adapter;internal uint Id,ModeIndex,OutputTechnology,Rotation,Scaling;
            internal uint RefreshNumerator,RefreshDenominator,ScanlineOrdering;internal int Available;internal uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)] struct PathInfo {internal SourceInfo Source;internal TargetInfo Target;internal uint Flags;}
        [StructLayout(LayoutKind.Explicit,Size=64)] struct ModeInfo {[FieldOffset(0)] internal uint Type;}
        [StructLayout(LayoutKind.Sequential)] struct Header {internal uint Type,Size;internal AdapterLuid Adapter;internal uint Id;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct SourceName {
            internal Header Header;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]internal string Name;
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct TargetName {
            internal Header Header;internal uint Flags,OutputTechnology;internal ushort Manufacturer,Product;internal uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]internal string FriendlyName;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]internal string DevicePath;
        }
        [StructLayout(LayoutKind.Sequential)] struct ColorInfo {internal Header Header;internal uint Flags,Encoding,Bits,Mode;}
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags,ref uint pathCount,[Out]PathInfo[] paths,ref uint modeCount,[Out]ModeInfo[] modes,IntPtr topology);
        [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetSourceName(ref SourceName name);
        [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetTargetName(ref TargetName name);
        [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetColorInfo(ref ColorInfo info);
        internal static List<DisplayPath> Enumerate()
        {
            for(int attempt=0;attempt<4;attempt++) {
                uint paths,modes;Check(GetDisplayConfigBufferSizes(2,out paths,out modes));
                if(paths>256 || modes>4096)throw new InvalidOperationException("디스플레이 연결 목록 크기가 올바르지 않습니다.");
                if(paths==0)return new List<DisplayPath>();
                var pathInfo=new PathInfo[paths];var modeInfo=new ModeInfo[Math.Max(1,modes)];
                int result=QueryDisplayConfig(2,ref paths,pathInfo,ref modes,modeInfo,IntPtr.Zero);
                if(result==122)continue;
                Check(result);var list=new List<DisplayPath>();
                for(int i=0;i<paths;i++) {
                    var path=pathInfo[i];if((path.Flags&1)==0 || path.Target.Available==0)continue;
                    var source=new SourceName {Header=new Header {Type=1,Size=(uint)Marshal.SizeOf(typeof(SourceName)),Adapter=path.Source.Adapter,Id=path.Source.Id}};
                    var target=new TargetName {Header=new Header {Type=2,Size=(uint)Marshal.SizeOf(typeof(TargetName)),Adapter=path.Target.Adapter,Id=path.Target.Id}};
                    Check(GetSourceName(ref source));Check(GetTargetName(ref target));
                    list.Add(new DisplayPath {GdiName=source.Name,InterfaceId=target.DevicePath,
                        Adapter=path.Target.Adapter,SourceId=path.Source.Id,TargetId=path.Target.Id});
                }
                return list;
            }
            throw new InvalidOperationException("모니터 연결이 계속 변경되어 ICC 대상을 확인하지 못했습니다.");
        }
        internal static DisplayPath Match(IEnumerable<DisplayPath> paths,DisplayTarget display)
        {
            var matches=paths.Where(p=>string.Equals(p.GdiName,display.GdiName,StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.InterfaceId,display.Key,StringComparison.OrdinalIgnoreCase)).ToList();
            if(matches.Count!=1)throw new InvalidOperationException("ICC를 적용할 모니터의 연결을 구분할 수 없습니다. 새로고침하세요.");
            return matches[0];
        }
        internal static DisplayPath Resolve(DisplayTarget display) {return Match(Enumerate(),display);}
        internal static string ColorMode(DisplayTarget display)
        {
            var path=Resolve(display);
            var info=new ColorInfo {Header=new Header {Type=15,Size=(uint)Marshal.SizeOf(typeof(ColorInfo)),Adapter=path.Adapter,Id=path.TargetId}};
            int result=GetColorInfo(ref info);
            if(result==0)return info.Mode==0?"SDR":info.Mode==1?"ACM / wide color":info.Mode==2?"HDR":"unknown "+info.Mode;
            // Older Windows exposes ACM and HDR through one shared enabled flag.
            info.Header.Type=9;info.Header.Size=32;result=GetColorInfo(ref info);
            if(result==0)return (info.Flags&2)!=0?"Advanced Color enabled (ACM or HDR)":"SDR";
            return "unknown (Windows error "+result+")";
        }
        static void Check(int code) {if(code!=0)throw new Win32Exception(code,"ICC 디스플레이 연결 조회에 실패했습니다.");}
        internal static bool ValidLayout {get {return Marshal.SizeOf(typeof(AdapterLuid))==8 && Marshal.SizeOf(typeof(PathInfo))==72
            && Marshal.SizeOf(typeof(ModeInfo))==64 && Marshal.SizeOf(typeof(SourceName))==84 && Marshal.SizeOf(typeof(TargetName))==420;}}
    }
}
