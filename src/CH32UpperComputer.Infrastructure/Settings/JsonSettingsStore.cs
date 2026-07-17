using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CH32UpperComputer.Infrastructure.Settings
{
    /// <summary>
    /// 使用带 BOM 的 UTF-8 JSON 文件原子保存应用设置，并隔离损坏或不兼容的旧文件。
    /// </summary>
    public sealed class JsonSettingsStore
    {
        /// <summary>
        /// 设置文件明确使用的带 BOM UTF-8 编码。
        /// </summary>
        private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

        /// <summary>
        /// 统一 JSON 属性和枚举表示的序列化选项。
        /// </summary>
        private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

        /// <summary>
        /// 串行化同一设置文件的加载、隔离和保存操作，避免临时文件与损坏文件命名竞态。
        /// </summary>
        private readonly SemaphoreSlim ioGate = new(1, 1);

        /// <summary>
        /// 为损坏文件名提供可测试的 UTC 时间。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 使用系统时间创建指定路径的设置存储。
        /// </summary>
        /// <param name="filePath">设置 JSON 文件的绝对或相对路径。</param>
        public JsonSettingsStore(string filePath)
            : this(filePath, TimeProvider.System)
        {
        }

        /// <summary>
        /// 使用指定时间源创建设置存储。
        /// </summary>
        /// <param name="filePath">设置 JSON 文件的绝对或相对路径。</param>
        /// <param name="timeProvider">生成损坏文件时间戳的统一时间源。</param>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> 为空时抛出。</exception>
        public JsonSettingsStore(
            string filePath,
            TimeProvider timeProvider)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("设置文件路径不能为空。", nameof(filePath));
            }

            ArgumentNullException.ThrowIfNull(timeProvider);
            FilePath = Path.GetFullPath(filePath.Trim());
            this.timeProvider = timeProvider;
        }

        /// <summary>
        /// 获取设置 JSON 文件的规范化绝对路径。
        /// </summary>
        public string FilePath { get; }

        /// <summary>
        /// 加载并校验设置；文件缺失返回默认值，损坏或不兼容文件改名为带时间戳的 .corrupt 后返回默认值。
        /// </summary>
        /// <param name="cancellationToken">取消尚未完成的文件读取和等待存储门。</param>
        /// <returns>自动发送和定时发送均关闭的已校验独立设置。</returns>
        public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!File.Exists(FilePath))
                {
                    return AppSettings.CreateDefault();
                }

                try
                {
                    string json = await File.ReadAllTextAsync(
                        FilePath,
                        Encoding.UTF8,
                        cancellationToken).ConfigureAwait(false);
                    AppSettings settings = JsonSerializer.Deserialize<AppSettings>(
                        json,
                        SerializerOptions) ?? throw new JsonException("设置文件没有生成有效对象。");
                    return settings.CreateValidatedCopy();
                }
                catch (Exception exception) when (IsCorruptSettingsException(exception))
                {
                    QuarantineCorruptFileUnderGate();
                    return AppSettings.CreateDefault();
                }
            }
            finally
            {
                ioGate.Release();
            }
        }

        /// <summary>
        /// 将已校验安全副本先写入同目录临时文件，再原子替换目标设置文件。
        /// </summary>
        /// <param name="settings">需要保存的非空应用设置。</param>
        /// <param name="cancellationToken">取消尚未完成的等待、序列化或临时文件写入。</param>
        /// <returns>目标文件已经以带 BOM UTF-8 完整替换后完成的任务。</returns>
        public async Task SaveAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settings);
            AppSettings safeSettings = settings.CreateValidatedCopy();
            string json = JsonSerializer.Serialize(safeSettings, SerializerOptions);
            await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string? temporaryPath = null;

            try
            {
                string? directory = Path.GetDirectoryName(FilePath);

                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                temporaryPath = Path.Combine(
                    directory ?? string.Empty,
                    $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(
                    temporaryPath,
                    json,
                    Utf8WithBom,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, FilePath, true);
                temporaryPath = null;
            }
            finally
            {
                if (temporaryPath is not null && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                ioGate.Release();
            }
        }

        /// <summary>
        /// 创建采用缩进 JSON、稳定枚举字符串和大小写不敏感读取的序列化选项。
        /// </summary>
        /// <returns>供全部加载和保存操作共享的只读使用选项。</returns>
        private static JsonSerializerOptions CreateSerializerOptions()
        {
            JsonSerializerOptions options = new()
            {
                AllowTrailingCommas = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                WriteIndented = true,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        /// <summary>
        /// 判断异常是否表示文件内容损坏、结构不兼容或设置值越界。
        /// </summary>
        /// <param name="exception">加载和校验阶段捕获的异常。</param>
        /// <returns>应隔离原文件并恢复默认值时返回 <see langword="true"/>。</returns>
        private static bool IsCorruptSettingsException(Exception exception)
        {
            return exception is JsonException or InvalidDataException or ArgumentException;
        }

        /// <summary>
        /// 在已持有文件门时把损坏设置移动为带 UTC 时间戳且不会覆盖旧证据的 .corrupt 文件。
        /// </summary>
        private void QuarantineCorruptFileUnderGate()
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            string timestamp = timeProvider
                .GetUtcNow()
                .UtcDateTime
                .ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            string basePath = $"{FilePath}.{timestamp}.corrupt";
            string candidate = basePath;
            int suffix = 0;

            while (File.Exists(candidate))
            {
                suffix = checked(suffix + 1);
                candidate = $"{basePath}.{suffix.ToString(CultureInfo.InvariantCulture)}";
            }

            File.Move(FilePath, candidate, false);
        }
    }
}
