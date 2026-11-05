using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Ncp
{
    static class Program
    {
        internal static string SettingsDirectory(bool demo)
        {
            string directory=AppDomain.CurrentDomain.BaseDirectory;
            return demo?Path.Combine(directory,"demo-data"):directory;
        }
        [STAThread]
        static int Main(string[] args)
        {
            if(args.Length==2 && args[0]=="--install-icc")return WindowsIccBackend.InstallFromCommandLine(args[1]);
            if(args.Length==2 && args[0]=="--clean-icc")return WindowsIccBackend.CleanupFromCommandLine(args[1]);
            if(Array.IndexOf(args,"--self-test")>=0) return SelfTests.Run();
            if(Array.IndexOf(args,"--diagnose")>=0) return Diagnostics.Run();
            bool demo=Array.IndexOf(args,"--demo")>=0;
            bool created;
            using(var mutex=new Mutex(true,"Local\\NCP.ManualColorPresets"+(demo?".Demo":""),out created)) {
                if(!created) {MessageBox.Show("NCP가 이미 실행 중입니다. 작업 표시줄에서 기존 창을 열어주세요.","NVIDIA Color Preset");return 0;}
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                IColorBackend backend=null; string error=null;
                try {backend=demo?(IColorBackend)new DemoBackend():new NvidiaBackend();}
                catch(Exception e) {error=e.Message;}
                string path=SettingsDirectory(demo);
                using(var controller=new Controller(new Store(path,true),backend,demo?(IIccBackend)new DemoIccBackend():new WindowsIccBackend()))
                using(var window=new MainForm(controller,error,demo)) Application.Run(window);
                mutex.ReleaseMutex();
            }
            return 0;
        }
    }
    public sealed class DemoBackend:IColorBackend
    {
        ColorValues current=new ColorValues();
        public System.Collections.Generic.List<DisplayTarget> Enumerate() {return new System.Collections.Generic.List<DisplayTarget> {
            new DisplayTarget {Key="demo-monitor",GdiName=@"\\.\DISPLAY1",Name="DEMO DISPLAY",Primary=true}};}
        public DisplayReading Read(DisplayTarget d) {return new DisplayReading {Vibrance=current.Vibrance,Hue=current.Hue};}
        public void Apply(DisplayTarget d,ColorValues v,double[] channels,ColorValues rollback,double[] rc) {current=v.Copy();}
        public void Dispose() {}
    }
}
