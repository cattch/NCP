using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Ncp
{
    // NVIDIA stores the desktop RGB controls as nine DWORDs plus a restore flag.
    // See https://github.com/pbatard/nvBrightness/blob/master/src/nvDisplay.cpp
    internal sealed class NvidiaPanelGamma
    {
        internal const int ChannelCount = 9;
        internal const string RestoreFlag = "NvCplGammaSet";
        internal const string ReferenceMode = "3538970";
        readonly Func<string,int?> read;
        readonly Action<string,int?> write;
        readonly int[] requested;
        readonly int?[] previous;
        readonly List<int> attempted = new List<int>();

        internal NvidiaPanelGamma(Func<string,int?> read, Action<string,int?> write, int[] values)
        {
            this.read = read;
            this.write = write;
            requested = values.Concat(new[] {1}).ToArray();
            previous = Enumerable.Range(0, requested.Length).Select(i => read(Name(i))).ToArray();
        }

        internal static string KeyPath(uint deviceId)
        {
            return @"Software\NVIDIA Corporation\Global\NVTweak\Devices\" +
                deviceId.ToString(CultureInfo.InvariantCulture) + @"-0\Color";
        }

        internal static string Name(int index)
        {
            return index == ChannelCount ? RestoreFlag : (3538946 + index).ToString(CultureInfo.InvariantCulture);
        }

        internal static int? ReadDword(RegistryKey key, string name)
        {
            object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null) return null;
            if (!(value is int) || key.GetValueKind(name) != RegistryValueKind.DWord)
                throw new InvalidDataException("NVIDIA 제어판 저장값의 형식이 올바르지 않습니다.");
            return (int)value;
        }

        internal static int[] Encode(ColorValues values, double[] channels)
        {
            var raw = new int[ChannelCount];
            for (int i = 0; i < raw.Length; i++) {
                double value = channels == null ? (i < 3 ? values.Brightness : i < 6 ? values.Contrast : values.Gamma) : channels[i];
                double scaled = i < 6 ? 80 + value / 2.5 : value * 100;
                raw[i] = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
            }
            return raw;
        }

        internal static double[] Decode(int[] raw)
        {
            return raw.Select((value, i) => i < 6 ? (value - 80) * 2.5 : value / 100.0).ToArray();
        }

        internal void Save()
        {
            if (read(ReferenceMode) == 2) throw new IOException("NVIDIA 참조 모드가 적용 도중 켜졌습니다.");
            for (int i = 0; i < requested.Length; i++) {
                string name = Name(i);
                if (read(name) != previous[i]) throw new IOException("NVIDIA 제어판 저장값이 적용 도중 변경되었습니다.");
                if (previous[i] == requested[i]) continue;
                attempted.Add(i);
                write(name, requested[i]);
            }
        }

        internal bool Matches()
        {
            return read(ReferenceMode) != 2 && Enumerable.Range(0, requested.Length).All(i => read(Name(i)) == requested[i]);
        }

        internal bool Restore()
        {
            bool restored = true;
            for (int position = attempted.Count - 1; position >= 0; position--) {
                int i = attempted[position];
                string name = Name(i);
                try {
                    int? current = read(name);
                    if (current == previous[i]) continue;
                    if (current == requested[i]) write(name, previous[i]);
                    else restored = false;
                } catch { restored = false; }
            }
            return restored;
        }
    }
}
