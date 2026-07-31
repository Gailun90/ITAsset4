using System;
using System.Collections.Generic;
using System.Text;
using ITAsset4.Common;
using Xunit;

namespace ITAsset4.Logic.Tests
{
    public class InferSilentArgsTests
    {
        [Theory]
        [InlineData("C:\\Program Files\\App\\uninstaller.exe", "NSIS 卸载器补 /S")]
        [InlineData("C:\\App\\helper.exe", "helper.exe 视为 NSIS 补 /S")]
        [InlineData("C:\\App\\uninstall.exe", "uninstall.exe 视为 NSIS 补 /S")]
        public void Nsis_AppendsSlashS(string exe, string _)
        {
            var r = InstallerArgInference.InferSilentArgs("/somearg", exe);
            Assert.EndsWith("/S", r);
            Assert.DoesNotContain("/SILENT", r);
        }

        [Theory]
        [InlineData("C:\\App\\unins000.exe")]
        [InlineData("C:\\App\\unins001.exe")]
        public void InnoSetup_AppendsInnoSilentFlags(string exe)
        {
            var r = InstallerArgInference.InferSilentArgs("", exe);
            Assert.Contains("/SILENT", r);
            Assert.Contains("/VERYSILENT", r);
            Assert.Contains("/SUPPRESSMSGBOXES", r);
        }

        [Theory]
        [InlineData("C:\\App\\foo.msi")]
        [InlineData("C:\\Windows\\System32\\msiexec.exe")]
        public void Msi_AppendsQuietNoRestart(string exe)
        {
            var r = InstallerArgInference.InferSilentArgs("", exe);
            Assert.Contains("/quiet", r);
            Assert.Contains("/norestart", r);
        }

        [Fact]
        public void Other_Installer_FallsBackToQuietNoRestart()
        {
            var r = InstallerArgInference.InferSilentArgs("", "C:\\App\\setup.exe");
            Assert.Contains("/quiet", r);
            Assert.Contains("/norestart", r);
        }

        [Fact]
        public void AlreadySilent_NotDuplicated()
        {
            // 修复前会出现重复 /SILENT 分支；现在原样返回，不重复追加
            var r = InstallerArgInference.InferSilentArgs("/SILENT /VERYSILENT", "C:\\App\\setup.exe");
            Assert.Equal("/SILENT /VERYSILENT", r);
        }

        [Fact]
        public void DetectInstallerKind_ClassifiesCorrectly()
        {
            Assert.Equal(InstallerKind.Nsis, InstallerArgInference.DetectInstallerKind("x\\uninstaller.exe"));
            Assert.Equal(InstallerKind.InnoSetup, InstallerArgInference.DetectInstallerKind("x\\unins000.exe"));
            Assert.Equal(InstallerKind.Msi, InstallerArgInference.DetectInstallerKind("x\\foo.msi"));
            Assert.Equal(InstallerKind.Other, InstallerArgInference.DetectInstallerKind("x\\setup.exe"));
        }
    }

    public class RebootGuardTests
    {
        [Fact]
        public void RestartService_IsAllowed()
        {
            // 之前会被 \brestart\b 误杀；现用负向先行断言排除 Restart-Service
            Assert.False(RebootGuard.ContainsRebootShutdown("Restart-Service -Name Spooler"));
        }

        [Fact]
        public void WriteHostWithRebootText_IsAllowed()
        {
            // 字符串字面量里的 "will reboot" 不应触发
            Assert.False(RebootGuard.ContainsRebootShutdown("Write-Host \"will reboot later\""));
        }

        [Fact]
        public void ShutdownCommand_IsBlocked()
        {
            Assert.True(RebootGuard.ContainsRebootShutdown("shutdown /r /t 0"));
        }

        [Fact]
        public void RestartComputer_IsBlocked()
        {
            Assert.True(RebootGuard.ContainsRebootShutdown("Restart-Computer -Force"));
        }

        [Fact]
        public void BareReboot_IsBlocked()
        {
            Assert.True(RebootGuard.ContainsRebootShutdown("reboot"));
        }

        [Fact]
        public void CommentedShutdown_IsAllowed()
        {
            // 注释里的 shutdown 不应触发
            Assert.False(RebootGuard.ContainsRebootShutdown("# 注意：不要 shutdown /r"));
        }
    }

    public class ScriptSanitizerTests
    {
        [Fact]
        public void StripsCommentLinesButKeepsRealCommands()
        {
            var script = "Write-Host hi\n# a comment\nRestart-Service Foo";
            var r = ScriptSanitizer.StripComments(script);
            Assert.Contains("Write-Host hi", r);
            Assert.Contains("Restart-Service Foo", r);
            Assert.DoesNotContain("# a comment", r);
        }

        [Fact]
        public void InlineCommentOnRealCommand_IsRemoved()
        {
            var r = ScriptSanitizer.StripComments("echo hello # trailing note");
            Assert.Equal("echo hello ", r);
        }
    }

    public class ScriptEncodingTests
    {
        [Fact]
        public void Bat_HasNoBom()
        {
            var enc = (UTF8Encoding)ScriptEncoding.ForExtension("bat");
            Assert.False(enc.GetPreamble().Length > 0);
        }

        [Fact]
        public void Ps1_HasBom()
        {
            var enc = (UTF8Encoding)ScriptEncoding.ForExtension("ps1");
            Assert.True(enc.GetPreamble().Length == 3);
        }
    }

    public class TaskDedupTests
    {
        [Fact]
        public void SameIdWithinWindow_IsDuplicate()
        {
            var d = new TaskDedup(TimeSpan.FromMinutes(15));
            Assert.False(d.TryAcquire(42));   // 第一次领取
            Assert.True(d.TryAcquire(42));    // 窗口内重复
        }

        [Fact]
        public void MarkFailed_AllowsReacquire()
        {
            var d = new TaskDedup(TimeSpan.FromMinutes(15));
            d.TryAcquire(7);
            d.MarkFailed(7);
            Assert.False(d.TryAcquire(7));
        }

        [Fact]
        public async System.Threading.Tasks.Task ConcurrentAcquire_ExactlyOneWins()
        {
            var d = new TaskDedup(TimeSpan.FromMinutes(15));
            int acquired = 0, dup = 0;
            var tasks = new List<System.Threading.Tasks.Task>();
            for (int i = 0; i < 50; i++)
            {
                tasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    if (d.TryAcquire(99)) System.Threading.Interlocked.Increment(ref dup);
                    else System.Threading.Interlocked.Increment(ref acquired);
                }));
            }
            await System.Threading.Tasks.Task.WhenAll(tasks.ToArray());
            // 只有一个线程领取成功（返回 false），其余 49 个判定为重复（返回 true）
            Assert.Equal(1, acquired);
            Assert.Equal(49, dup);
        }
    }

    public class DownloadResumeTests
    {
        [Fact]
        public void ServerReturns200_WithRangeRequest_MustOverwrite_NotAppend()
        {
            // 核心修复：带 Range 却被返回 200（服务器忽略 Range）→ 禁止追加，从 0 覆盖
            var (append, offset) = DownloadResume.Decide(100, true, 200);
            Assert.False(append);
            Assert.Equal(0L, offset);
        }

        [Fact]
        public void Status206_Appends()
        {
            var (append, offset) = DownloadResume.Decide(100, true, 206);
            Assert.True(append);
            Assert.Equal(100L, offset);
        }

        [Fact]
        public void Status416_Overwrites()
        {
            var (append, offset) = DownloadResume.Decide(100, true, 416);
            Assert.False(append);
            Assert.Equal(0L, offset);
        }

        [Fact]
        public void NoLocalFragment_Overwrites()
        {
            var (append, offset) = DownloadResume.Decide(0, false, 200);
            Assert.False(append);
            Assert.Equal(0L, offset);
        }
    }

    public class InferSilentArgsEdgeTests
    {
        [Fact]
        public void BareSlashS_NotDuplicated()
        {
            // F1：已带 /S 时不应再追加 /S，避免 "/S /S"
            var r = InstallerArgInference.InferSilentArgs("/S", "C:\\App\\uninstaller.exe");
            Assert.Equal("/S", r);
        }

        [Fact]
        public void BareSlashS_WithOtherArgs_NotDuplicated()
        {
            var r = InstallerArgInference.InferSilentArgs("/S /L=install.log", "C:\\App\\uninstaller.exe");
            Assert.Contains("/S", r);
            Assert.DoesNotContain("/S /S", r);
        }
    }

    public class RebootGuardLauncherTests
    {
        [Fact]
        public void CmdWrapperShutdown_IsBlocked()
        {
            // F2：cmd /c "shutdown /r" 之前会被引号剥离绕过，现应被拦截
            Assert.True(RebootGuard.ContainsRebootShutdown("cmd /c \"shutdown /r /t 0\""));
        }

        [Fact]
        public void PowerShellWrapperRestartComputer_IsBlocked()
        {
            Assert.True(RebootGuard.ContainsRebootShutdown("powershell -Command \"Restart-Computer -Force\""));
        }
    }

    public class TaskDedupInProgressTests
    {
        [Fact]
        public void InProgress_PreventsReacquireWithinWindow()
        {
            var d = new TaskDedup(TimeSpan.FromMinutes(15));
            Assert.False(d.TryAcquire(5));   // 首次领取
            Assert.True(d.TryAcquire(5));    // 执行中再次派发 → 重复，禁止并发双跑
        }

        [Fact]
        public void Completed_WithinWindow_IsDuplicate()
        {
            var d = new TaskDedup(TimeSpan.FromMinutes(15));
            d.TryAcquire(5);
            d.MarkCompleted(5);
            Assert.True(d.TryAcquire(5));    // 完成后窗口内仍为重复
        }

        [Fact]
        public void Failed_AllowsImmediateReacquire()
        {
            var d = new TaskDedup(TimeSpan.FromMinutes(15));
            d.TryAcquire(5);
            d.MarkFailed(5);
            Assert.False(d.TryAcquire(5));   // 失败后允许立即重新派发
        }
    }
}
