using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TelemetryMonitor
{
    // libpq owns authentication and the PostgreSQL protocol. No database I/O runs on Monitor.Gate.
    internal sealed class HistoryPostgresClient : IDisposable
    {
        private static readonly object LoaderGate=new object();
        private static IntPtr module;
        private IntPtr connection;
        private readonly object cancellationGate=new object();
        private IntPtr cancellation;
        private bool cancellationRequested;
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr LoadLibraryEx(string file,IntPtr reserved,uint flags);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr PQconnectdbParams(IntPtr[] keywords,IntPtr[] values,int expand);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int PQstatus(IntPtr conn);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr PQerrorMessage(IntPtr conn);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern void PQfinish(IntPtr conn);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr PQexec(IntPtr conn,IntPtr query);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr PQexecParams(IntPtr conn,IntPtr query,int count,IntPtr types,IntPtr[] values,IntPtr lengths,IntPtr formats,int resultFormat);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int PQsendQuery(IntPtr conn,IntPtr query);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int PQsendQueryParams(IntPtr conn,IntPtr query,int count,IntPtr types,IntPtr[] values,IntPtr lengths,IntPtr formats,int resultFormat);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr PQgetResult(IntPtr conn);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int PQresultStatus(IntPtr result);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int PQntuples(IntPtr result);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int PQnfields(IntPtr result);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr PQfname(IntPtr result,int column);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr PQgetvalue(IntPtr result,int row,int column);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int PQgetisnull(IntPtr result,int row,int column);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr PQresultErrorMessage(IntPtr result);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern void PQclear(IntPtr result);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr PQcancelCreate(IntPtr conn);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern void PQcancelReset(IntPtr cancel);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int PQcancelStart(IntPtr cancel);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int PQcancelPoll(IntPtr cancel);
        [DllImport("libpq.dll",CallingConvention=CallingConvention.Cdecl)]private static extern void PQcancelFinish(IntPtr cancel);
        private static IntPtr Utf8(string value)
        {if(value==null)return IntPtr.Zero;byte[] bytes=Encoding.UTF8.GetBytes(value+"\0");var memory=Marshal.AllocHGlobal(bytes.Length);Marshal.Copy(bytes,0,memory,bytes.Length);return memory;}
        private static string Read(IntPtr value)
        {if(value==IntPtr.Zero)return "";int length=0;while(Marshal.ReadByte(value,length)!=0)length++;byte[] bytes=new byte[length];Marshal.Copy(value,bytes,0,length);return Encoding.UTF8.GetString(bytes);}
        internal HistoryPostgresClient(string root,HistoryStorageConfiguration configuration,bool cancellable=false)
        {
            lock(LoaderGate)if(module==IntPtr.Zero)
            {
                if(IntPtr.Size!=8)throw new InvalidOperationException("历史数据库客户端需要64位Windows进程");
                string path=Path.Combine(root,"native","postgresql","libpq.dll");
                if(!File.Exists(path))throw new InvalidOperationException("缺少离线数据库客户端，请完整覆盖升级包中的native目录");
                module=LoadLibraryEx(path,IntPtr.Zero,0x00000100|0x00001000);if(module==IntPtr.Zero)throw new InvalidOperationException("数据库客户端加载失败："+new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
            var keys=new[]{"host","port","dbname","user","password","connect_timeout","client_encoding","application_name","options","sslmode"};
            var values=new[]{configuration.Host,configuration.Port.ToString(),configuration.Database,configuration.Username,configuration.Password(),"3","UTF8","TelemetryHistory","-c statement_timeout=15000 -c lock_timeout=3000 -c timezone=UTC -c client_min_messages=warning","prefer"};
            var allocated=new List<IntPtr>();try
            {
                var k=new IntPtr[keys.Length+1];var v=new IntPtr[keys.Length+1];for(int i=0;i<keys.Length;i++){k[i]=Utf8(keys[i]);v[i]=Utf8(values[i]);allocated.Add(k[i]);allocated.Add(v[i]);}
                connection=PQconnectdbParams(k,v,0);if(connection==IntPtr.Zero||PQstatus(connection)!=0){string message=Read(PQerrorMessage(connection));Dispose();throw new InvalidOperationException("数据库连接失败："+message.Trim());}
                if(cancellable){cancellation=PQcancelCreate(connection);if(cancellation==IntPtr.Zero){Dispose();throw new InvalidOperationException("无法准备历史维护取消连接");}}
            }
            finally{foreach(var p in allocated)Marshal.FreeHGlobal(p);}
        }
        internal List<Dictionary<string,string>> Query(string sql,params string[] arguments)
        {
            var values=new IntPtr[arguments.Length];IntPtr query=Utf8(sql),result=IntPtr.Zero;
            try
            {
                for(int i=0;i<arguments.Length;i++)values[i]=Utf8(arguments[i]);
                if(cancellation!=IntPtr.Zero)
                {
                    // Publish the command before cancellation can be sent. Waiting for its result
                    // happens outside this lock, so shutdown can cancel a blocked maintenance SQL.
                    lock(cancellationGate)
                    {
                        if(cancellationRequested)throw new OperationCanceledException("历史维护已停止");
                        int sent=arguments.Length==0?PQsendQuery(connection,query):PQsendQueryParams(connection,query,arguments.Length,IntPtr.Zero,values,IntPtr.Zero,IntPtr.Zero,0);
                        if(sent!=1)throw new InvalidOperationException("数据库操作失败："+Read(PQerrorMessage(connection)).Trim());
                    }
                    result=PQgetResult(connection);
                }
                else result=arguments.Length==0?PQexec(connection,query):PQexecParams(connection,query,arguments.Length,IntPtr.Zero,values,IntPtr.Zero,IntPtr.Zero,0);
                int status=result==IntPtr.Zero?-1:PQresultStatus(result);if(status!=1&&status!=2)throw new InvalidOperationException("数据库操作失败："+(result==IntPtr.Zero?Read(PQerrorMessage(connection)):Read(PQresultErrorMessage(result))).Trim());
                var rows=new List<Dictionary<string,string>>();int columns=PQnfields(result);var names=new string[columns];for(int c=0;c<columns;c++)names[c]=Read(PQfname(result,c));
                for(int r=0;r<PQntuples(result);r++){var row=new Dictionary<string,string>();for(int c=0;c<columns;c++)row[names[c]]=PQgetisnull(result,r,c)!=0?null:Read(PQgetvalue(result,r,c));rows.Add(row);}return rows;
            }
            finally{if(result!=IntPtr.Zero){PQclear(result);if(cancellation!=IntPtr.Zero){IntPtr rest;while((rest=PQgetResult(connection))!=IntPtr.Zero)PQclear(rest);}}Marshal.FreeHGlobal(query);foreach(var p in values)if(p!=IntPtr.Zero)Marshal.FreeHGlobal(p);}
        }
        // Uses a separate libpq cancellation connection. The query's owning thread still
        // performs PQclear/PQfinish after PQexec returns; another thread never frees it.
        internal bool Cancel(int timeoutMilliseconds=2000)
        {
            lock(cancellationGate)
            {
                if(cancellation==IntPtr.Zero)return false;cancellationRequested=true;PQcancelReset(cancellation);if(PQcancelStart(cancellation)==0)return false;
                var elapsed=System.Diagnostics.Stopwatch.StartNew();
                while(elapsed.ElapsedMilliseconds<Math.Max(1,timeoutMilliseconds))
                {
                    int status=PQcancelPoll(cancellation);if(status==3)return true;if(status==0)return false;
                    System.Threading.Thread.Sleep(5);
                }
                // Abandoning a nonblocking cancellation closes only its own connection.
                PQcancelReset(cancellation);return false;
            }
        }
        public void Dispose()
        {
            lock(cancellationGate){if(cancellation!=IntPtr.Zero){PQcancelFinish(cancellation);cancellation=IntPtr.Zero;}if(connection!=IntPtr.Zero){PQfinish(connection);connection=IntPtr.Zero;}}
        }
    }
}
