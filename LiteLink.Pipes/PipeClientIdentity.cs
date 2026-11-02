using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LiteLink.Pipes
{
    /// <summary>
    /// Kernel-asserted identity of a connected pipe client, resolved by the server at accept time.
    ///
    /// Threat model: the user identity (UserName / Uid) is asserted by the operating system and
    /// cannot be forged by the client — it is real authentication, suitable for an approved-user
    /// list. The process id is also kernel-asserted, but any conclusion drawn from it (executable
    /// path, code signature) is only hardening: on both platforms a process can be influenced or
    /// injected into by other processes of the same user, and a PID can be reused after exit.
    /// Do not treat process-based checks as a security boundary; verify payloads instead
    /// (see the auto-update guidance in the project documentation).
    /// </summary>
    public class PipeClientIdentity
    {
        /// <summary>
        /// account name of the client process' user, or null when it could not be resolved.
        /// On Windows this is the account name without domain; on Unix the name for the peer uid
        /// </summary>
        public string UserName { get; init; }

        /// <summary>client process id (Windows and Linux), or null when unavailable</summary>
        public int? ProcessId { get; init; }

        /// <summary>peer user id (Linux only), or null</summary>
        public int? Uid { get; init; }

        /// <summary>peer group id (Linux only), or null</summary>
        public int? Gid { get; init; }

        internal static PipeClientIdentity Resolve(NamedPipeServerStream pipe, Action<Exception> reportError)
        {
            string userName = null;
            int? processId = null;
            int? uid = null;
            int? gid = null;

            try
            {
                //implemented by the BCL on both Windows (pipe handle state) and Unix (peer uid lookup)
                userName = pipe.GetImpersonationUserName();
            }
            catch (Exception ex)
            {
                reportError?.Invoke(ex);
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
                        processId = (int)pid;
                }
                else if (OperatingSystem.IsLinux())
                {
                    //on Linux the pipe is a Unix domain socket, SO_PEERCRED yields pid/uid/gid
                    var cred = default(UCred);
                    var len = Marshal.SizeOf<UCred>();
                    if (getsockopt((int)pipe.SafePipeHandle.DangerousGetHandle(), SOL_SOCKET, SO_PEERCRED, ref cred, ref len) == 0)
                    {
                        processId = cred.Pid;
                        uid = cred.Uid;
                        gid = cred.Gid;
                    }
                }
            }
            catch (Exception ex)
            {
                reportError?.Invoke(ex);
            }

            return new PipeClientIdentity
            {
                UserName = userName,
                ProcessId = processId,
                Uid = uid,
                Gid = gid
            };
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        private const int SOL_SOCKET = 1;
        private const int SO_PEERCRED = 17;

        [StructLayout(LayoutKind.Sequential)]
        private struct UCred
        {
            public int Pid;
            public int Uid;
            public int Gid;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int getsockopt(int sockfd, int level, int optname, ref UCred optval, ref int optlen);
    }
}
