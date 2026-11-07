using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ncp
{
    internal sealed class MonitorIdentity
    {
        internal string Name,Description,InterfaceId,RegistryKey;
        internal uint Flags;
    }
    internal static class WindowsDisplayIdentity
    {
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Device
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Description;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Key;
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool EnumDisplayDevices(string name,uint index,ref Device device,uint flags);
        internal static List<MonitorIdentity> Enumerate(string gdiName)
        {
            var result=new List<MonitorIdentity>();
            for(uint i=0;i<32;i++) {
                var identified=new Device {Size=Marshal.SizeOf(typeof(Device))};
                if(!EnumDisplayDevices(gdiName,i,ref identified,1))break;
                var standard=new Device {Size=Marshal.SizeOf(typeof(Device))};
                bool paired=EnumDisplayDevices(gdiName,i,ref standard,0)
                    && string.Equals(standard.Name,identified.Name,StringComparison.OrdinalIgnoreCase);
                result.Add(new MonitorIdentity {Name=identified.Name,Description=identified.Description,
                    InterfaceId=identified.Id,RegistryKey=paired?standard.Key:identified.Key,Flags=paired?standard.Flags:identified.Flags});
            }
            return result;
        }
        internal static MonitorIdentity FindActive(IEnumerable<MonitorIdentity> monitors,string expectedId=null)
        {
            var matches=monitors.Where(m=>(m.Flags&1)!=0 && !string.IsNullOrWhiteSpace(m.InterfaceId)
                && (expectedId==null || string.Equals(m.InterfaceId,expectedId,StringComparison.OrdinalIgnoreCase))).ToList();
            if(matches.Count>1)throw new InvalidOperationException("같은 디스플레이에 여러 활성 모니터가 연결되어 대상을 구분할 수 없습니다.");
            return matches.SingleOrDefault();
        }
        internal static string ResolveKey(DisplayTarget display)
        {
            var monitor=FindActive(Enumerate(display.GdiName),display.Key);
            if(monitor==null || string.IsNullOrWhiteSpace(monitor.RegistryKey))
                throw new InvalidOperationException("ICC를 적용할 모니터의 연결이 변경되었습니다. 새로고침하세요.");
            return monitor.RegistryKey;
        }
    }
}
