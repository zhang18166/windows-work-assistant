using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WindowsWorkAssistant
{
    /// <summary>管理内置 PDF 服务。匿名管道只连接本次子进程；不开放网络端口。</summary>
    public sealed class EngineClient : IDisposable
    {
        private Process _process;
        private JobOwner _job;
        private StreamWriter _writer;
        private readonly object _inputLock = new object();
        public event Action<Dictionary<string, object>> Message;
        public event Action<string> Failure;
        public bool Running { get { return _process != null && !_process.HasExited; } }

        private static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
        public static string ExtractEngine()
        {
            byte[] data;
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ExcelPdfEngine.exe"))
            using (var buffer = new MemoryStream())
            { if (resource == null) throw new IOException("缺少内置转换引擎"); resource.CopyTo(buffer); data = buffer.ToArray(); }
            if (Hash(data) != BuildInfo.EngineSha256) throw new IOException("内置转换引擎校验失败");
            string directory = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowsWorkAssistant", "runtime", BuildInfo.Version+"-"+BuildInfo.EngineSha256.Substring(0,16));
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "ExcelPdfEngine.exe");
            if (File.Exists(target) && Hash(File.ReadAllBytes(target)) == BuildInfo.EngineSha256) return target;
            string temp = target+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                File.WriteAllBytes(temp,data);
                if (File.Exists(target)) File.Replace(temp,target,null); else File.Move(temp,target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return target;
        }
        public void Start()
        {
            if (Running) return;
            string exe = ExtractEngine();
            _job = new JobOwner();
            _process = new Process();
            _process.StartInfo = new ProcessStartInfo(exe) { UseShellExecute=false, CreateNoWindow=true,
                RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8, StandardErrorEncoding=Encoding.UTF8,
                WorkingDirectory=Path.GetDirectoryName(exe) };
            _process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            _process.Start();
            try { _job.Assign(_process.Handle); }
            catch { try { _process.Kill(); } catch { } Dispose(); throw; }
            _writer = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush=true };
            Task.Run((Action)ReadMessages);
            Task.Run(delegate { try { string error = _process.StandardError.ReadToEnd(); if (!string.IsNullOrWhiteSpace(error)) TraceLog.Write(error); } catch { } });
        }
        private void ReadMessages()
        {
            try
            {
                string line;
                while ((line = _process.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length > 1024*1024) throw new IOException("转换服务消息过长");
                    var data = new JavaScriptSerializer { MaxJsonLength=1024*1024 }.Deserialize<Dictionary<string,object>>(line);
                    Action<Dictionary<string,object>> callback = Message;
                    if (callback != null) callback(data);
                }
                if (!_disposing) { Action<string> failed=Failure; if (failed!=null) failed("转换服务意外退出，请关闭整合程序后重开；输入法模块仍可使用。"); }
            }
            catch (Exception ex) { if (!_disposing) { Action<string> failed=Failure; if (failed!=null) failed(ex.Message); } }
        }
        public void Send(string command, Dictionary<string,object> config = null)
        {
            lock (_inputLock)
            {
                if (!Running) throw new IOException("转换服务未运行");
                var request = new Dictionary<string,object> { {"command",command}, {"id",Guid.NewGuid().ToString("N")} };
                if (config!=null) request["config"]=config;
                // StreamWriter 默认 UTF-8；不经命令行传递路径，避免引号及中文转义问题。
                _writer.WriteLine(new JavaScriptSerializer().Serialize(request));
            }
        }
        private volatile bool _disposing;
        public async Task StopAsync()
        {
            _disposing=true;
            try { if (Running) Send("quit"); } catch { }
            await Task.Run(delegate { if (Running) _process.WaitForExit(12000); });
            Dispose();
        }
        public void Dispose()
        {
            _disposing=true;
            try { if (_writer!=null) _writer.Dispose(); } catch { }
            _writer=null;
            if (_job!=null) { _job.Dispose(); _job=null; }
            // 只关闭本对象创建的进程/句柄，绝不按 EXCEL.EXE 名称终止进程。
            if (_process!=null) { _process.Dispose(); _process=null; }
        }
    }

    /// <summary>关闭宿主时只清理其引擎及后代。Excel 核心还保留原有独立 Job 清理。</summary>
    internal sealed class JobOwner : IDisposable
    {
        private IntPtr _handle;
        public JobOwner()
        {
            _handle=CreateJobObject(IntPtr.Zero,null);
            if (_handle==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var info=new ExtendedLimit(); info.Basic.LimitFlags=0x2000; // KILL_ON_JOB_CLOSE
            int size=Marshal.SizeOf(info); IntPtr memory=Marshal.AllocHGlobal(size);
            try { Marshal.StructureToPtr(info,memory,false); if(!SetInformationJobObject(_handle,9,memory,(uint)size)) throw new System.ComponentModel.Win32Exception(); }
            catch { Dispose(); throw; } finally { Marshal.FreeHGlobal(memory); }
        }
        public void Assign(IntPtr process) { if(!AssignProcessToJobObject(_handle,process)) throw new System.ComponentModel.Win32Exception(); }
        public void Dispose() { if(_handle!=IntPtr.Zero) { CloseHandle(_handle);_handle=IntPtr.Zero; } }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet,MaxWorkingSet;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint Priority,Scheduling; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory; }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr attributes,string name);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job,int type,IntPtr info,uint length);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
