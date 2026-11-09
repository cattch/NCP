// SPDX-License-Identifier: GPL-3.0-only
// NVIDIA ABI declarations and gamma calculation adapted from:
// mgcarnevali/NvProfileSwitcher, Nvidia/NvApi.cs, commit 7aabfb679c76da702afb6c398b47c46074dbefdd.
// NCP modifications (2026-10-27): manual-only operations, strict validation,
// System32-only loading, per-channel restore and rollback.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Ncp
{
    public sealed class DisplayTarget
    {
        public string GdiName, Name, Key;
        public uint Id;
        public IntPtr Handle;
        public bool Primary;
        public override string ToString() { return Name+" · "+GdiName.Replace(@"\\.\","")+(Primary?" · 주 모니터":""); }
    }
    public sealed class DisplayReading
    {
        public int Vibrance, Hue;
        public ColorValues PanelValues;
        public double[] Channels;
        public string Fingerprint;
        public string PanelReadIssue;
        public string PanelSaveIssue;
        public double[] AppliedChannels;
    }
    public interface IColorBackend : IDisposable
    {
        List<DisplayTarget> Enumerate();
        DisplayReading Read(DisplayTarget display);
        DisplayReading Apply(DisplayTarget display, ColorValues values, double[] channels, ColorValues rollback, double[] rollbackChannels);
    }

    public sealed class NvidiaBackend : IColorBackend
    {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
        [DllImport("kernel32.dll", CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Query(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Simple();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HandleFn([MarshalAs(UnmanagedType.LPStr)] string name,out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IdFn([MarshalAs(UnmanagedType.LPStr)] string name,out uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DvcFn(IntPtr display,uint output,ref Dvc info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HueFn(IntPtr display,uint output,ref Hue info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetHueFn(IntPtr display,uint output,uint angle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GammaFn(uint id,ref GammaData data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LuidFn(uint id,uint flags,[Out] uint[] luid);
        [StructLayout(LayoutKind.Sequential)] struct Dvc { public uint Version; public int Current,Min,Max,Default; }
        [StructLayout(LayoutKind.Sequential)] struct Hue { public uint Version,Current,Default; }
        [StructLayout(LayoutKind.Sequential,Pack=1)] struct GammaData {
            public uint Version;
            [MarshalAs(UnmanagedType.ByValArray,SizeConst=3072)] public float[] Ramp;
            public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct DisplayDevice {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Description;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Key;
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool EnumDisplayDevices(string name,uint index,ref DisplayDevice device,uint flags);
        IntPtr module;
        Query query;
        Simple unload;
        HandleFn getHandle;
        IdFn getId;
        DvcFn getDvc,setDvc;
        HueFn getHue;
        SetHueFn setHue;
        GammaFn setGamma;
        LuidFn getLuid;
        bool initialized;

        public NvidiaBackend()
        {
            module=LoadLibraryEx(Path.Combine(Environment.SystemDirectory,"nvapi64.dll"),IntPtr.Zero,0x800);
            if(module==IntPtr.Zero) throw new InvalidOperationException("NVIDIA 드라이버를 불러올 수 없습니다. NVIDIA GPU와 드라이버 설치를 확인하세요.");
            try {
                var p=GetProcAddress(module,"nvapi_QueryInterface");
                if(p==IntPtr.Zero) throw new InvalidOperationException("NVAPI 진입점을 찾지 못했습니다.");
                query=(Query)Marshal.GetDelegateForFunctionPointer(p,typeof(Query));
                var init=Resolve<Simple>(0x0150E828);
                unload=Resolve<Simple>(0xD22BDD7E);
                Check(init(),"NVIDIA 초기화"); initialized=true;
                getHandle=Resolve<HandleFn>(0x35C29134); getId=Resolve<IdFn>(0xAE457190);
                getDvc=Resolve<DvcFn>(0x0E45002D); setDvc=Resolve<DvcFn>(0x4A82C2B1);
                getHue=Resolve<HueFn>(0x95B64341); setHue=Resolve<SetHueFn>(0xF5A0F22C);
                setGamma=Resolve<GammaFn>(0x7082A053);
                if(query(0xD4A859F2)!=IntPtr.Zero) getLuid=Resolve<LuidFn>(0xD4A859F2);
            } catch { Dispose(); throw; }
        }
        T Resolve<T>(uint id) where T:class
        {
            var p=query(id);
            if(p==IntPtr.Zero) throw new InvalidOperationException("드라이버가 필요한 색상 기능을 지원하지 않습니다. NVAPI 0x"+id.ToString("X8"));
            return Marshal.GetDelegateForFunctionPointer(p,typeof(T)) as T;
        }
        static void Check(int code,string operation)
        {
            if(code!=0) throw new InvalidOperationException(operation+" 실패 (NVAPI "+code+"). 드라이버 / 디스플레이 연결 상태를 확인하세요.");
        }
        public List<DisplayTarget> Enumerate()
        {
            var list=new List<DisplayTarget>();
            for(uint i=0;i<64;i++) {
                var dd=new DisplayDevice { Size=Marshal.SizeOf(typeof(DisplayDevice)) };
                if(!EnumDisplayDevices(null,i,ref dd,0)) break;
                if((dd.Flags&1)==0 || (dd.Flags&8)!=0) continue;
                IntPtr handle; uint id;
                string nv=dd.Name.StartsWith(@"\\.\",StringComparison.Ordinal)?@"\\"+dd.Name.Substring(4):dd.Name;
                if(getHandle(dd.Name,out handle)!=0 && getHandle(nv,out handle)!=0) continue;
                if(getId(dd.Name,out id)!=0 && getId(nv,out id)!=0) continue;
                var mon=WindowsDisplayIdentity.FindActive(WindowsDisplayIdentity.Enumerate(dd.Name));
                // A remembered inactive child may precede the actual connected monitor.
                if(mon==null)continue;
                list.Add(new DisplayTarget { GdiName=dd.Name,Name=mon.Description,Key=mon.InterfaceId,
                    Id=id,Handle=handle,Primary=(dd.Flags&4)!=0 });
            }
            return list.OrderByDescending(d=>d.Primary).ToList();
        }
        Dvc ReadDvc(DisplayTarget display)
        {
            var d=new Dvc {Version=0x10014}; Check(getDvc(display.Handle,0,ref d),"채도 조회");
            if(d.Min>=d.Max || d.Default<=d.Min || d.Default>=d.Max || d.Current<d.Min || d.Current>d.Max)
                throw new InvalidOperationException("드라이버가 알 수 없는 채도 범위를 반환했습니다.");
            return d;
        }
        public DisplayReading Read(DisplayTarget display)
        {
            var d=ReadDvc(display); var h=new Hue {Version=0x1000C}; Check(getHue(display.Handle,0,ref h),"색조 조회");
            if(h.Current>359) throw new InvalidOperationException("색조 조회 결과가 올바르지 않습니다.");
            int v=(int)Math.Round(d.Current>=d.Default ? 50+50.0*(d.Current-d.Default)/(d.Max-d.Default) : 50-50.0*(d.Default-d.Current)/(d.Default-d.Min));
            var result=new DisplayReading {Vibrance=v,Hue=(int)h.Current};
            try {ReadPanel(display,result);}
            catch(System.Security.SecurityException) {result.PanelReadIssue="제어판 저장값에 대한 읽기 권한이 없습니다.";}
            catch(UnauthorizedAccessException) {result.PanelReadIssue="제어판 저장값에 대한 읽기 권한이 없습니다.";}
            catch(IOException) {result.PanelReadIssue="제어판 저장값을 읽지 못했습니다.";}
            return result;
        }
        uint? PanelDeviceId(DisplayTarget display)
        {
            if(getLuid==null) return null;
            var luid=new uint[4];
            if(getLuid(display.Id,1,luid)!=0 || luid[1]==0) return null;
            uint id=luid[1]^0xF0000000;
            return id==0?(uint?)null:id;
        }
        void ReadPanel(DisplayTarget display,DisplayReading result)
        {
            var id=PanelDeviceId(display);
            if(!id.HasValue)return;
            string keyPath=NvidiaPanelGamma.KeyPath(id.Value);
            using(var key=Registry.CurrentUser.OpenSubKey(keyPath,false)) {
                if(key==null) return;
                var raw=new int[NvidiaPanelGamma.ChannelCount];
                for(int i=0;i<raw.Length;i++) {
                    int? value=NvidiaPanelGamma.ReadDword(key,NvidiaPanelGamma.Name(i));
                    if(!value.HasValue)return;
                    raw[i]=value.Value;
                    if(i<6 ? raw[i]<80 || raw[i]>120 : raw[i]<30 || raw[i]>280) return;
                }
                // Reference mode can bypass the sliders; do not claim to have captured it as a color preset.
                if(NvidiaPanelGamma.ReadDword(key,NvidiaPanelGamma.ReferenceMode)==2)return;
                var values=NvidiaPanelGamma.Decode(raw);
                result.Channels=values;
                result.Fingerprint=string.Join(",",raw.Select(x=>x.ToString(CultureInfo.InvariantCulture)));
                result.PanelValues=new ColorValues {Brightness=values[0],Contrast=values[3],Gamma=values[6],Vibrance=result.Vibrance,Hue=result.Hue};
            }
        }
        public static float[] BuildRamp(ColorValues values,double[] channels)
        {
            values.Validate();
            if(channels!=null && channels.Length!=9) throw new InvalidDataException("원본 RGB 데이터 길이가 올바르지 않습니다.");
            var ramp=new float[3072];
            for(int c=0;c<3;c++) {
                double b=channels==null?values.Brightness:channels[c];
                double contrast=channels==null?values.Contrast:channels[3+c];
                double gamma=channels==null?values.Gamma:channels[6+c];
                new ColorValues {Brightness=b,Contrast=contrast,Gamma=gamma}.Validate();
                // Present 0..100 to the user; source driver's nominal 80..120 maps to -0.2..+0.2.
                double cn=(contrast-50)/250.0, shift=(b-50)/250.0;
                for(int i=0;i<1024;i++) {
                    double x=i/1023.0;
                    double y=cn<=0 ? (cn+1)*(x-.5) : (x-.5)/(1-cn);
                    y=Math.Max(0,Math.Min(1,y+shift+.5));
                    ramp[i*3+c]=(float)Math.Pow(y,1/gamma);
                }
            }
            return ramp;
        }
        void WriteGamma(DisplayTarget display,ColorValues values,double[] channels)
        {
            var data=new GammaData {Ramp=BuildRamp(values,channels),Flags=1};
            // 4-byte version + 3072 floats + 4-byte flags = 12296 bytes.
            data.Version=(uint)(Marshal.SizeOf(typeof(GammaData))|0x10000);
            Check(setGamma(display.Id,ref data),"밝기 / 대비 / 감마 적용");
        }
        public DisplayReading Apply(DisplayTarget display,ColorValues values,double[] channels,ColorValues rollback,double[] rollbackChannels)
        {
            values.Validate(); BuildRamp(values,channels); // Validate every value before the first write.
            if(rollback!=null) BuildRamp(rollback,rollbackChannels);
            // Resolve a fresh handle and identity before writing; do not send values to a reused DISPLAY number.
            var displays=Enumerate();
            var fresh=displays.SingleOrDefault(x=>string.Equals(x.Key,display.Key,StringComparison.OrdinalIgnoreCase));
            if(fresh==null) throw new InvalidOperationException("선택한 모니터의 연결이 변경되었습니다. 모니터 목록을 새로고침하세요.");
            display=fresh;
            var oldDvc=ReadDvc(display); var oldHue=new Hue {Version=0x1000C};
            Check(getHue(display.Handle,0,ref oldHue),"변경 전 색조 조회");
            var desired=oldDvc;
            desired.Current=(int)Math.Round(values.Vibrance>=50 ? oldDvc.Default+(values.Vibrance-50)/50.0*(oldDvc.Max-oldDvc.Default)
                : oldDvc.Default-(50-values.Vibrance)/50.0*(oldDvc.Default-oldDvc.Min));
            RegistryKey panelKey=null;
            NvidiaPanelGamma panelWrite=null;
            string panelIssue=null;
            var deviceId=PanelDeviceId(display);
            if(!deviceId.HasValue || displays.Any(d=>d!=fresh && PanelDeviceId(d)==deviceId)) {
                panelIssue="제어판의 모니터별 저장 위치를 확인할 수 없습니다.";
            } else {
                try {
                    panelKey=Registry.CurrentUser.CreateSubKey(NvidiaPanelGamma.KeyPath(deviceId.Value));
                    if(panelKey==null)throw new IOException("NVIDIA 제어판 저장 위치를 열 수 없습니다.");
                    if(NvidiaPanelGamma.ReadDword(panelKey,NvidiaPanelGamma.ReferenceMode)==2) {
                        panelIssue="NVIDIA 참조 모드가 켜져 있습니다.";
                    } else {
                        var raw=NvidiaPanelGamma.Encode(values,channels);
                        panelWrite=new NvidiaPanelGamma(name=>NvidiaPanelGamma.ReadDword(panelKey,name),
                            (name,value)=> {
                                if(value.HasValue)panelKey.SetValue(name,value.Value,RegistryValueKind.DWord);
                                else panelKey.DeleteValue(name,false);
                            },raw);
                        channels=NvidiaPanelGamma.Decode(raw);
                    }
                } catch(Exception error) {
                    if(!(error is System.Security.SecurityException || error is UnauthorizedAccessException || error is IOException)) {
                        if(panelKey!=null)panelKey.Dispose();
                        throw;
                    }
                    panelIssue="NVIDIA 제어판 저장값에 접근할 수 없습니다.";
                }
            }
            bool gammaAttempted=false;
            try {
                Check(setDvc(display.Handle,0,ref desired),"채도 적용");
                Check(setHue(display.Handle,0,(uint)values.Hue),"색조 적용");
                if(panelWrite!=null)panelWrite.Save();
                gammaAttempted=true; WriteGamma(display,values,channels);
                var check=Read(display);
                if(Math.Abs(check.Vibrance-values.Vibrance)>1 || check.Hue!=values.Hue)
                    throw new InvalidOperationException("적용 후 채도 / 색조가 요청한 값과 일치하지 않습니다.");
                if(panelWrite!=null && !panelWrite.Matches())
                    throw new IOException("NVIDIA 제어판의 밝기 / 대비 / 감마 저장값이 일치하지 않습니다.");
                check.AppliedChannels=channels;
                check.PanelSaveIssue=panelIssue;
                return check;
            } catch(Exception failure) {
                var errors=new List<string>();
                if(panelWrite!=null && !panelWrite.Restore())errors.Add("NVIDIA 제어판 저장값");
                if(setDvc(display.Handle,0,ref oldDvc)!=0) errors.Add("채도");
                if(setHue(display.Handle,0,oldHue.Current)!=0) errors.Add("색조");
                if(gammaAttempted) {
                    if(rollback==null) errors.Add("밝기 / 대비 / 감마 (변경 전 실시간 값을 읽을 수 없음)");
                    else try { WriteGamma(display,rollback,rollbackChannels); } catch { errors.Add("밝기 / 대비 / 감마"); }
                }
                throw new InvalidOperationException(failure.Message+"\n"+(errors.Count==0?"변경 전 설정으로 복원 요청했습니다.":"복원을 확인할 수 없는 항목: "+string.Join(", ",errors))+"\n현재 상태를 확인하고 필요하면 기본값을 적용하세요.",failure);
            } finally {if(panelKey!=null)panelKey.Dispose();}
        }
        public void Dispose()
        {
            if(initialized && unload!=null) { unload(); initialized=false; }
            if(module!=IntPtr.Zero) { FreeLibrary(module); module=IntPtr.Zero; }
        }
    }
}
