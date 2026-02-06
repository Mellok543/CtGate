using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace CtGateRecruitment;

public record CtGateAnswerInstance(string Text, bool IsCorrect);
public record CtGateQuestionInstance(string Text, IReadOnlyList<CtGateAnswerInstance> Answers);
public record CtGateBlockInfo(DateTime? BlockedUntil, string? BlockReason);

public class CtGateBlockInfoRow
{
    public DateTime? BlockedUntil { get; set; }
    public string? BlockReason { get; set; }
}

public class CtGateTestSession
{
    public CtGateTestSession(List<CtGateQuestionInstance> questions)
    {
        Questions = questions;
    }

    public List<CtGateQuestionInstance> Questions { get; }
    public int CurrentIndex { get; set; }
}

[MinimumApiVersion(80)]
public class CtGateRecruitmentPlugin : BasePlugin, IPluginConfig<CtGateConfig>
{
    public override string ModuleName => "CT Gate Recruitment";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "CtGate";
    public override string ModuleDescription => "CT recruitment with quiz, queue, and MySQL-backed cooldowns.";

    public CtGateConfig Config { get; set; } = new();

    private readonly Dictionary<ulong, CtGateTestSession> _sessions = new();
    private readonly Queue<ulong> _ctQueue = new();
    private readonly HashSet<ulong> _ctQueueSet = new();
    private readonly HashSet<ulong> _pendingTransfers = new();
    private readonly CtGateBalanceService _balanceService = new();

    public void OnConfigParsed(CtGateConfig config)
    {
        if (config.CtPerT < 1)
        {
            config.CtPerT = 1;
        }

        if (config.BlockMinutes < 1)
        {
            config.BlockMinutes = 1;
        }

        if (config.QueueTransferDelaySeconds < 0)
        {
            config.QueueTransferDelaySeconds = 0;
        }

        if (config.QueueCheckIntervalSeconds < 1)
        {
            config.QueueCheckIntervalSeconds = 1;
        }

        Config = config;
    }

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServerHandler);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnectHandler);

        AddCommandListener("jointeam", OnJoinTeamCommand);
        AddTimer(Config.QueueCheckIntervalSeconds, ProcessQueue, TimerFlags.REPEAT);

        Task.Run(EnsureDatabaseAsync);
    }

    private void OnClientPutInServerHandler(int slot, string name, string ipAddress)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid || player.IsBot)
        {
            return;
        }

        if (player.Team == CsTeam.Spectator)
        {
            return;
        }

        if (player.Team != CsTeam.Terrorist)
        {
            player.SwitchTeam(CsTeam.Terrorist);
        }

        Task.Run(() => EnsurePlayerRowAsync(player.SteamID));
    }

    private void OnClientDisconnectHandler(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null)
        {
            return;
        }

        var steamId = player.SteamID;
        _sessions.Remove(steamId);
        _pendingTransfers.Remove(steamId);
        if (_ctQueueSet.Remove(steamId))
        {
            RebuildQueueWithout(steamId);
        }
    }

    private HookResult OnJoinTeamCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null)
        {
            return HookResult.Continue;
        }

        if (string.IsNullOrWhiteSpace(commandInfo.ArgString))
        {
            return HookResult.Continue;
        }

        if (!int.TryParse(commandInfo.ArgString.Split(' ')[0], out var team))
        {
            return HookResult.Continue;
        }

        if (team == (int)CsTeam.Terrorist || team == (int)CsTeam.Spectator)
        {
            _sessions.Remove(player.SteamID);
            _pendingTransfers.Remove(player.SteamID);
            if (_ctQueueSet.Remove(player.SteamID))
            {
                RebuildQueueWithout(player.SteamID);
            }

            player.SwitchTeam((CsTeam)team);
            return HookResult.Handled;
        }

        if (team != (int)CsTeam.CounterTerrorist)
        {
            return HookResult.Continue;
        }

        player.PrintToChat($"{Config.ChatPrefix} Для перехода в КТ используйте /ct.");
        return HookResult.Handled;
    }

    [ConsoleCommand("css_ct", "Запуск теста на вступление в КТ")]
    public void OnCtCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null || !player.IsValid)
        {
            commandInfo.ReplyToCommand("Команда доступна только игрокам.");
            return;
        }

        if (player.IsBot)
        {
            commandInfo.ReplyToCommand("Боты не могут проходить тест.");
            return;
        }

        if (player.Team == CsTeam.CounterTerrorist)
        {
            player.PrintToChat($"{Config.ChatPrefix} Вы уже на стороне КТ.");
            return;
        }

        var steamId = player.SteamID;
        if (_sessions.ContainsKey(steamId))
        {
            player.PrintToChat($"{Config.ChatPrefix} Вы уже проходите тест.");
            return;
        }

        if (_ctQueueSet.Contains(steamId))
        {
            player.PrintToChat($"{Config.ChatPrefix} Вы уже в очереди на КТ.");
            return;
        }

        Task.Run(async () =>
        {
            var blockInfo = await GetBlockInfoAsync(steamId);
            if (blockInfo.BlockedUntil.HasValue && blockInfo.BlockedUntil.Value > DateTime.UtcNow)
            {
                var remaining = blockInfo.BlockedUntil.Value - DateTime.UtcNow;
                var reason = string.IsNullOrWhiteSpace(blockInfo.BlockReason)
                    ? "без указания причины"
                    : blockInfo.BlockReason;
                Server.NextFrame(() =>
                {
                    player.PrintToChat(
                        $"{Config.ChatPrefix} Команда /ct заблокирована ещё на {Math.Ceiling(remaining.TotalMinutes)} мин. Причина: {reason}.");
                });
                return;
            }

            Server.NextFrame(() => StartTest(player));
        });
    }

    private void StartTest(CCSPlayerController player)
    {
        if (Config.Questions.Count < 4)
        {
            player.PrintToChat($"{Config.ChatPrefix} В конфиге должно быть минимум 4 вопроса.");
            return;
        }

        var random = Random.Shared;
        var selected = Config.Questions
            .OrderBy(_ => random.Next())
            .Take(4)
            .Select(question => BuildQuestionInstance(question, random))
            .ToList();

        var session = new CtGateTestSession(selected);
        _sessions[player.SteamID] = session;

        ShowQuestion(player, session);
    }

    private CtGateQuestionInstance BuildQuestionInstance(CtGateQuestionConfig question, Random random)
    {
        var indexedAnswers = question.Answers
            .Select((answer, index) => new { answer, index });
        if (Config.ShuffleAnswers)
        {
            indexedAnswers = indexedAnswers.OrderBy(_ => random.Next());
        }

        var answers = indexedAnswers
            .Select(entry => new CtGateAnswerInstance(entry.answer, entry.index == question.CorrectAnswerIndex))
            .ToList();

        return new CtGateQuestionInstance(question.Text, answers);
    }

    private void ShowQuestion(CCSPlayerController player, CtGateTestSession session)
    {
        if (!player.IsValid)
        {
            _sessions.Remove(player.SteamID);
            return;
        }

        var question = session.Questions[session.CurrentIndex];
        var menu = new ChatMenu($"Вопрос {session.CurrentIndex + 1}/{session.Questions.Count}: {question.Text}")
        {
            ExitButton = false,
            PostSelectAction = PostSelectAction.Close
        };

        for (var i = 0; i < question.Answers.Count; i++)
        {
            var index = i;
            var answer = question.Answers[index];
            menu.AddMenuOption($"{index + 1}. {answer.Text}", (p, _) =>
            {
                if (!_sessions.TryGetValue(p.SteamID, out var activeSession))
                {
                    return;
                }

                if (activeSession.CurrentIndex != session.CurrentIndex)
                {
                    return;
                }

                if (!answer.IsCorrect)
                {
                    FailTest(p);
                    return;
                }

                activeSession.CurrentIndex++;
                if (activeSession.CurrentIndex >= activeSession.Questions.Count)
                {
                    PassTest(p);
                    return;
                }

                Server.NextFrame(() => ShowQuestion(p, activeSession));
            });
        }

        MenuManager.OpenChatMenu(player, menu);
    }

    private void FailTest(CCSPlayerController player)
    {
        _sessions.Remove(player.SteamID);
        var blockedUntil = DateTime.UtcNow.AddMinutes(Config.BlockMinutes);
        const string reason = "Неверный ответ в тесте";

        Task.Run(() => SetBlockAsync(player.SteamID, blockedUntil, reason));

        player.PrintToChat(
            $"{Config.ChatPrefix} Тест провален. Команда /ct заблокирована на {Config.BlockMinutes} мин.");
    }

    private void PassTest(CCSPlayerController player)
    {
        _sessions.Remove(player.SteamID);
        TryMoveToCt(player);
    }

    private void TryMoveToCt(CCSPlayerController player)
    {
        if (!player.IsValid)
        {
            return;
        }

        if (IsCtSlotAvailable())
        {
            MovePlayerToCt(player);
            return;
        }

        EnqueueForCt(player);
    }

    private void EnqueueForCt(CCSPlayerController player)
    {
        if (_ctQueueSet.Add(player.SteamID))
        {
            _ctQueue.Enqueue(player.SteamID);
            player.PrintToChat($"{Config.ChatPrefix} Вы в очереди на КТ. Позиция: {_ctQueue.Count}.");
        }
    }

    private void ProcessQueue()
    {
        if (_ctQueue.Count == 0)
        {
            return;
        }

        while (_ctQueue.Count > 0)
        {
            var steamId = _ctQueue.Peek();
            var player = Utilities.GetPlayerFromSteamId64(steamId);
            if (player == null || !player.IsValid)
            {
                _ctQueue.Dequeue();
                _ctQueueSet.Remove(steamId);
                _pendingTransfers.Remove(steamId);
                continue;
            }

            if (player.Team == CsTeam.CounterTerrorist)
            {
                _ctQueue.Dequeue();
                _ctQueueSet.Remove(steamId);
                _pendingTransfers.Remove(steamId);
                continue;
            }

            if (!IsCtSlotAvailable())
            {
                return;
            }

            if (_pendingTransfers.Contains(steamId))
            {
                return;
            }

            _pendingTransfers.Add(steamId);
            var delay = Math.Max(0, Config.QueueTransferDelaySeconds);
            AddTimer(delay, () =>
            {
                _pendingTransfers.Remove(steamId);
                if (!_ctQueueSet.Contains(steamId))
                {
                    return;
                }

                var queuedPlayer = Utilities.GetPlayerFromSteamId64(steamId);
                if (queuedPlayer == null || !queuedPlayer.IsValid)
                {
                    _ctQueueSet.Remove(steamId);
                    RebuildQueueWithout(steamId);
                    return;
                }

                if (!IsCtSlotAvailable())
                {
                    return;
                }

                MovePlayerToCt(queuedPlayer);
            });

            return;
        }
    }

    private void MovePlayerToCt(CCSPlayerController player)
    {
        player.SwitchTeam(CsTeam.CounterTerrorist);
        player.PrintToChat($"{Config.ChatPrefix} Вы переведены в КТ.");

        if (_ctQueueSet.Remove(player.SteamID))
        {
            RebuildQueueWithout(player.SteamID);
        }
    }

    private bool IsCtSlotAvailable()
    {
        return _balanceService.IsCtSlotAvailable(Utilities.GetPlayers(), Config.CtPerT);
    }

    private void RebuildQueueWithout(ulong steamId)
    {
        if (_ctQueue.Count == 0)
        {
            return;
        }

        var remaining = _ctQueue.Where(id => id != steamId).ToList();
        _ctQueue.Clear();
        foreach (var id in remaining)
        {
            _ctQueue.Enqueue(id);
        }
    }

    private async Task EnsureDatabaseAsync()
    {
        await using var connection = new MySqlConnection(BuildConnectionString());
        await connection.OpenAsync();

        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS players (
                steam_id VARCHAR(32) NOT NULL,
                ct_attempts_blocked_until DATETIME NULL,
                block_reason VARCHAR(255) NULL,
                PRIMARY KEY (steam_id)
            );");

        Logger.LogInformation("CT Gate database ensured.");
    }

    private async Task EnsurePlayerRowAsync(ulong steamId)
    {
        await using var connection = new MySqlConnection(BuildConnectionString());
        await connection.OpenAsync();

        await connection.ExecuteAsync(@"
            INSERT INTO players (steam_id)
            VALUES (@SteamId)
            ON DUPLICATE KEY UPDATE steam_id = steam_id;",
            new { SteamId = steamId.ToString() });
    }

    private async Task<CtGateBlockInfo> GetBlockInfoAsync(ulong steamId)
    {
        await using var connection = new MySqlConnection(BuildConnectionString());
        await connection.OpenAsync();

        var result = await connection.QueryFirstOrDefaultAsync<CtGateBlockInfoRow>(@"
            SELECT ct_attempts_blocked_until AS BlockedUntil,
                   block_reason AS BlockReason
            FROM players
            WHERE steam_id = @SteamId;",
            new { SteamId = steamId.ToString() });

        if (result == null)
        {
            return new CtGateBlockInfo(null, null);
        }

        return new CtGateBlockInfo(result.BlockedUntil, result.BlockReason);
    }

    private async Task SetBlockAsync(ulong steamId, DateTime blockedUntil, string reason)
    {
        await using var connection = new MySqlConnection(BuildConnectionString());
        await connection.OpenAsync();

        await connection.ExecuteAsync(@"
            INSERT INTO players (steam_id, ct_attempts_blocked_until, block_reason)
            VALUES (@SteamId, @BlockedUntil, @Reason)
            ON DUPLICATE KEY UPDATE
                ct_attempts_blocked_until = @BlockedUntil,
                block_reason = @Reason;",
            new
            {
                SteamId = steamId.ToString(),
                BlockedUntil = blockedUntil,
                Reason = reason
            });
    }

    private string BuildConnectionString()
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Config.Host,
            Database = Config.Database,
            UserID = Config.User,
            Password = Config.Password,
            Port = uint.TryParse(Config.Port, out var port) ? port : 3306
        };

        return builder.ConnectionString;
    }
}
