using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Config;

namespace CtGateRecruitment;

public class CtGateQuestionConfig
{
    [JsonPropertyName("Text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("Answers")] public List<string> Answers { get; set; } = new();
    [JsonPropertyName("CorrectAnswerIndex")] public int CorrectAnswerIndex { get; set; }
}

public class CtGateConfig : BasePluginConfig
{
    [JsonPropertyName("ChatPrefix")] public string ChatPrefix { get; set; } = "[CT Gate]";

    // Database
    [JsonPropertyName("Host")] public string Host { get; set; } = "host";
    [JsonPropertyName("Database")] public string Database { get; set; } = "Database";
    [JsonPropertyName("User")] public string User { get; set; } = "User";
    [JsonPropertyName("Password")] public string Password { get; set; } = "Password";
    [JsonPropertyName("Port")] public string Port { get; set; } = "3306";

    [JsonPropertyName("CtPerT")] public int CtPerT { get; set; } = 3;
    [JsonPropertyName("BlockMinutes")] public int BlockMinutes { get; set; } = 60;
    [JsonPropertyName("QueueTransferDelaySeconds")] public int QueueTransferDelaySeconds { get; set; } = 3;
    [JsonPropertyName("QueueCheckIntervalSeconds")] public int QueueCheckIntervalSeconds { get; set; } = 5;
    [JsonPropertyName("ShuffleAnswers")] public bool ShuffleAnswers { get; set; }

    [JsonPropertyName("Questions")] public List<CtGateQuestionConfig> Questions { get; set; } =
        new()
        {
            new CtGateQuestionConfig
            {
                Text = "Что обязан делать КТ при начале раунда?",
                Answers = new List<string>
                {
                    "Проверить наличие приказов и соблюдать правила",
                    "Сразу идти на точки",
                    "Идти в одиночку на позиции Т",
                    "Выходить из игры"
                },
                CorrectAnswerIndex = 0
            },
            new CtGateQuestionConfig
            {
                Text = "Можно ли КТ убивать заключённых без причины?",
                Answers = new List<string>
                {
                    "Да, всегда",
                    "Да, если скучно",
                    "Нет, только при нарушении правил",
                    "Только с ножом"
                },
                CorrectAnswerIndex = 2
            },
            new CtGateQuestionConfig
            {
                Text = "Что делать, если заключённый не выполняет приказ?",
                Answers = new List<string>
                {
                    "Дать предупреждение и повторить приказ",
                    "Сразу убить",
                    "Покинуть сервер",
                    "Перевести себя в наблюдатели"
                },
                CorrectAnswerIndex = 0
            },
            new CtGateQuestionConfig
            {
                Text = "Что важно для КТ во время игры?",
                Answers = new List<string>
                {
                    "Соблюдать правила и контролировать ситуацию",
                    "Игнорировать чат",
                    "Играть только ножом",
                    "Сразу покинуть карту"
                },
                CorrectAnswerIndex = 0
            },
            new CtGateQuestionConfig
            {
                Text = "Разрешено ли КТ использовать команды без разрешения?",
                Answers = new List<string>
                {
                    "Да, всегда",
                    "Нет, нужно уважать старшего КТ",
                    "Да, только если никто не видит",
                    "Только при победе"
                },
                CorrectAnswerIndex = 1
            },
            new CtGateQuestionConfig
            {
                Text = "Что делать, если КТ допустил ошибку?",
                Answers = new List<string>
                {
                    "Признать и исправить",
                    "Скрыть ошибку",
                    "Покинуть сервер",
                    "Перейти за Т"
                },
                CorrectAnswerIndex = 0
            }
        };
}
