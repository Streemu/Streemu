using System.IO.Pipes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Streemu.Util;

/// <summary>
/// Link streemu://assistir/&lt;id da transmissão&gt;. O botão Assistir do Discord abre uma página do Worker, e a página
/// abre este link; o Windows entrega para o Streemu.exe registrado aqui.
/// </summary>
public static partial class AppLink
{
    public const string Scheme = "streemu";

    [GeneratedRegex(@"^streemu://assistir/([A-Za-z0-9]{6,40})/?$", RegexOptions.IgnoreCase)]
    private static partial Regex WatchRegex();

    [GeneratedRegex(@"^streemu://grupo/([A-Za-z0-9-]{16,24})/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GroupRegex();

    /// <summary>Alfabeto do convite (o mesmo do relay/sala.js): sem I, L, O, 0 e 1, que se confundem na leitura.</summary>
    const string AlfabetoConvite = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>Código de convite (16 letras e números, maiúsculo, sem hífens) de um link streemu://grupo/&lt;código&gt;, ou null. Aceita hífens e minúsculas.</summary>
    public static string? InviteFrom(string? arg)
    {
        var m = GroupRegex().Match(arg?.Trim() ?? "");
        return m.Success ? NormalizeInvite(m.Groups[1].Value) : null;
    }

    /// <summary>Convite digitado, colado com hífens ou como link https://servidor/g/&lt;código&gt;: devolve o código normalizado ou null.</summary>
    public static string? NormalizeInvite(string? texto)
    {
        var t = (texto ?? "").Trim();
        int g = t.LastIndexOf("/g/", StringComparison.OrdinalIgnoreCase);
        if (g >= 0) t = t[(g + 3)..];
        t = t.TrimEnd('/').Replace("-", "").Replace(" ", "").ToUpperInvariant();
        return t.Length == 16 && t.All(c => AlfabetoConvite.Contains(c)) ? t : null;
    }

    /// <summary>Id da transmissão de um link streemu://assistir/..., ou null.</summary>
    public static string? StreamIdFrom(string? arg)
    {
        var m = WatchRegex().Match(arg?.Trim() ?? "");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Registra streemu:// em HKCU (sem administrador) apontando para ESTE .exe. Roda a cada abertura de propósito:
    /// cada versão nova vem numa pasta nova do zip, e o link precisa abrir a versão que a pessoa usou por último.
    /// </summary>
    public static void Register()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Streemu.exe");
            string command = $"\"{exe}\" \"%1\"";
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme);
            if (key.OpenSubKey(@"shell\open\command")?.GetValue("") as string == command) return;
            key.SetValue("", "URL:Streemu");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var cmd = key.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
            Log.Info("Link streemu:// registrado para este Streemu.exe (o botão Assistir do Discord abre aqui).");
        }
        catch (Exception e) { Log.Info("Não consegui registrar o link streemu:// (" + e.Message + "); o botão Assistir do Discord não vai abrir o app."); }
    }

    /// <summary>
    /// discord-&lt;id do app&gt;:// é como o Discord abre o jogo quando alguém clica em "Participar" e o jogo está fechado
    /// (o mesmo registro que o discord-rpc oficial faz). Depois de abrir, o segredo chega pelo IPC (ACTIVITY_JOIN).
    /// </summary>
    public static void RegisterDiscordLaunch(string appId)
    {
        try
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Streemu.exe");
            string command = $"\"{exe}\" \"%1\"";
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\discord-" + appId);
            if (key.OpenSubKey(@"shell\open\command")?.GetValue("") as string == command) return;
            key.SetValue("", "URL:Run game " + appId + " protocol");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var cmd = key.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
        }
        catch (Exception e) { Log.Info("Não consegui registrar o Streemu no Discord (" + e.Message + "); o Participar só funciona com o app aberto."); }
    }

    /// <summary>Aberto pelo Discord (discord-&lt;id&gt;://): o pedido de assistir chega depois, pelo IPC.</summary>
    public static bool IsDiscordLaunch(string? arg) => arg?.StartsWith("discord-", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Um Streemu por usuário. Quem abre um segundo (ex.: clicou em Assistir no Discord com o app já aberto) entrega
/// os argumentos ao primeiro por um pipe nomeado e fecha; o primeiro abre o player.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    readonly string _pipeName;
    readonly CancellationTokenSource _cts = new();

    /// <summary>Argumentos que outra instância mandou (thread de fundo).</summary>
    public event Action<string[]>? ArgsReceived;
    /// <summary>
    /// Abriram OUTRO Streemu.exe (outra pasta = outra versão): esta instância precisa fechar para ele assumir.
    /// Motivo (teste de 2026-11-06 18h40): um amigo abriu o zip novo 3 vezes, mas um Streemu antigo escondido na
    /// bandeja recebia o pedido e o novo fechava; ele ficou testando a versão errada sem saber.
    /// </summary>
    public event Action<string>? ReplacedBy;

    public enum ForwardResult { Delivered, OldInstanceClosing, OldVersionNoReply, NoAnswer }

    SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex; _pipeName = pipeName;
        new Thread(ServeLoop) { IsBackground = true, Name = "single-instance" }.Start();
    }

    // string.GetHashCode muda a cada processo no .NET: o nome precisa ser igual nas duas instâncias
    static string BaseName => "streemu-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..12].ToLowerInvariant()
        + (Environment.GetEnvironmentVariable("STREEMU_INSTANCE") is { Length: > 0 } t ? "-" + t : ""); // testes não esbarram no app de verdade

    /// <summary>Fica com a vaga de instância única, ou devolve null se já existe um Streemu aberto.</summary>
    public static SingleInstance? TryAcquire()
    {
        var m = new Mutex(true, @"Local\" + BaseName, out bool created);
        if (created) return new SingleInstance(m, BaseName);
        m.Dispose();
        return null;
    }

    static string MyExe => Environment.ProcessPath ?? "";

    /// <summary>Manda os argumentos (e o caminho deste .exe) ao Streemu que já está aberto.</summary>
    public static ForwardResult Forward(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", BaseName, PipeDirection.InOut);
            pipe.Connect(3000);
            // leaveOpen: o leitor fechava o pipe antes do escritor terminar e a exceção escondia a resposta
            using var w = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var r = new StreamReader(pipe, leaveOpen: true);
            w.WriteLine(MyExe);
            w.WriteLine(string.Join('\n', args).Replace("\r", "").Replace("\n", "\u001f"));
            var answer = r.ReadLine();
            // versões de teste anteriores não respondem: não dá para pedir que fechem
            return answer switch { "saindo" => ForwardResult.OldInstanceClosing, "ok" => ForwardResult.Delivered, _ => ForwardResult.OldVersionNoReply };
        }
        // as versões de teste anteriores abrem o pipe só para leitura: conectar aqui falha com acesso negado
        catch (UnauthorizedAccessException) { return ForwardResult.OldVersionNoReply; }
        catch (Exception e) { Log.Info($"instância única: sem resposta ({e.GetType().Name}: {e.Message})"); return ForwardResult.NoAnswer; }
    }

    /// <summary>Espera a instância antiga fechar e fica com a vaga (null se ela não fechar a tempo).</summary>
    public static SingleInstance? WaitAcquire(TimeSpan timeout)
    {
        var m = new Mutex(false, @"Local\" + BaseName);
        try
        {
            if (m.WaitOne(timeout)) return new SingleInstance(m, BaseName);
        }
        catch (AbandonedMutexException) { return new SingleInstance(m, BaseName); }
        m.Dispose();
        return null;
    }

    void ServeLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                using var r = new StreamReader(server, leaveOpen: true);
                using var w = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
                string exe = r.ReadLine() ?? "";
                var line = r.ReadLine() ?? "";
                bool other = exe != "" && !string.Equals(exe, MyExe, StringComparison.OrdinalIgnoreCase);
                w.WriteLine(other ? "saindo" : "ok");
                if (other) ReplacedBy?.Invoke(exe);
                else ArgsReceived?.Invoke(line.Split('\u001f', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Log.Info("instância única: " + e.Message); Thread.Sleep(500); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
