using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using MQTTnet;
using MQTTnet.Client;
using System.Runtime.InteropServices;
using System.Drawing;

namespace TelegramAlert
{
    class Program
    {
        // ========== DllImport для иконки консоли ==========
        [DllImport("user32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        // =================================================
        private static Config? config;
        private static SpeechSynthesizer? tts;
        private static UserNotificationListener? listener;
        private static HashSet<uint> processedIds = new();
        private static HashSet<uint> pendingDeletes = new();
        private static Stats stats = new();
        private static DateTime startTime;
        private static bool isRunning = true;
        private static DateTime lastSpeechTime = DateTime.MinValue;
        private static bool isMuted = false;
        private static Config? oldConfig;
        
        // MQTT
        private static IMqttClient? mqttClient;
        private static bool mqttConnected = false;

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "Telegram Alert Monitor";
			try
			{
				var handle = GetConsoleWindow();
				var assembly = System.Reflection.Assembly.GetEntryAssembly();
				if (assembly != null)
				{
					var location = assembly.Location;
					if (!string.IsNullOrEmpty(location))
					{
						var icon = Icon.ExtractAssociatedIcon(location);
						if (icon != null)
						{
							SendMessage(handle, 0x0080, IntPtr.Zero, icon.Handle);
						}
					}
				}
			}
            catch { }
            config = LoadConfig();
            
            if (!ParseArgs(args))
                return;
            
            PrintBanner();
            InitTTS();
            LoadProcessedIds();
            
            // Инициализация MQTT
            await InitMqttAsync();
            
            if (config!.Мониторинг.StartMinimized)
            {
                Console.WriteLine("🪟 Запуск в свернутом режиме...");
            }
            
            await RunNotificationMonitor();
        }

        static async Task InitMqttAsync()
        {
            if (!config!.MQTT.Enabled || !config!.Дополнительно.MqttEnabled)
            {
                Console.WriteLine("📡 MQTT отключен в настройках\n");
                return;
            }

            try
            {
                var factory = new MqttFactory();
                mqttClient = factory.CreateMqttClient();

                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(config.MQTT.Broker, config.MQTT.Port)
                    .WithClientId(config.MQTT.ClientId)
                    .WithCleanSession()
                    .Build();

                var result = await mqttClient.ConnectAsync(options);
                
                if (result.ResultCode == MqttClientConnectResultCode.Success)
                {
                    mqttConnected = true;
                    Console.WriteLine($"✅ MQTT подключён к {config.MQTT.Broker}:{config.MQTT.Port}\n");
                }
                else
                {
                    Console.WriteLine($"⚠️ MQTT ошибка подключения: {result.ResultCode}\n");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ MQTT ошибка: {ex.Message}\n");
            }
        }

		static async Task PublishToMqttAsync(string sender, string message)
		{
			// Очистка сообщения для MQTT (отдельно от озвучки и логов)
			string mqttText = SanitizeForMqtt(sender, message);
			
			// Переменные для тестового режима
			int originalBytes = 0;
			int cleanedBytes = 0;
			
			// Вывод в консоль (если TestMode включён)
			if (config!.MQTT.TestMode)
			{
				originalBytes = Encoding.UTF8.GetBytes($"[Telegram] {sender}: {message}").Length;
				cleanedBytes = Encoding.UTF8.GetBytes(mqttText).Length;
				
				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine($"   🧪 [MQTT] Тестовый вывод:");
				Console.WriteLine($"      Исходный размер: {originalBytes} байт");
				Console.WriteLine($"      После очистки: {cleanedBytes} байт (лимит: {config.MQTT.MaxPayloadBytes})");
				Console.WriteLine($"      Текст: {mqttText}");
				Console.ResetColor();
			}
			
			// НОВЫЙ ФОРМАТ JSON (согласно рабочему образцу)
			var payload = new
			{
				from = config!.MQTT.FromNodeId,
				to = config.MQTT.ToNodeId,
				channel = config.MQTT.ChannelIndex,
				type = "sendtext",
				payload = mqttText,
				hopLimit = 3
			};
			
			string jsonPayload = JsonConvert.SerializeObject(payload);
			
			// === ДОБАВЛЕНО: ВЫВОД JSON В ТЕСТОВОМ РЕЖИМЕ ===
			if (config.MQTT.TestMode)
			{
				Console.WriteLine($"      JSON: {jsonPayload}");
			}
			// ===========================================
			
			// Реальная отправка (если Enabled = true)
			if (config.MQTT.Enabled && mqttClient != null && mqttConnected)
			{
				try
				{
					var applicationMessage = new MqttApplicationMessageBuilder()
						.WithTopic(config.MQTT.Topic)
						.WithPayload(Encoding.UTF8.GetBytes(jsonPayload))
						.WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
						.Build();
					
					await mqttClient.PublishAsync(applicationMessage);
					stats.MqttSent++;
					
					if (config.Дополнительно.DebugMode)
					{
						Console.WriteLine($"   📡 MQTT отправлено ({cleanedBytes}/{config.MQTT.MaxPayloadBytes} байт)");
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"   ⚠️ MQTT публикация: {ex.Message}");
				}
			}
		}

        /// <summary>
        /// Обрезает строку до указанного количества БАЙТ (UTF-8), не разрывая символы
        /// </summary>
        static string TruncateToBytes(string text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length <= maxBytes) return text;
            
            // Обрезаем до maxBytes
            Array.Resize(ref bytes, maxBytes);
            
            // Проверяем, не разорвали ли мы UTF-8 символ
            while (maxBytes > 0 && (bytes[maxBytes - 1] & 0xC0) == 0x80)
            {
                maxBytes--;
                Array.Resize(ref bytes, maxBytes);
            }
            
            string result = Encoding.UTF8.GetString(bytes);
            
            if (result.Length < text.Length)
                result += "...";
            
            return result;
        }

        /// <summary>
        /// Очистка сообщения для отправки в MQTT (без озвучки и логов)
        /// </summary>
        static string SanitizeForMqtt(string sender, string message)
        {
            //string text = $"[Telegram] {sender}: {message}"; //с отправителем
            string text = $"[Telegram] {message}";//без отправителя
            // Замена [Telegram] на TG:
            text = text.Replace("[Telegram]", "TG:");
            
            // Удаление эмодзи
            text = Regex.Replace(text, @"\p{Cs}", "");
            
            // Удаление лишних пробелов
            text = Regex.Replace(text, @"\s+", " ");
            
            text = text.Trim();
            
            int maxBytes = config?.MQTT.MaxPayloadBytes ?? 100;
            text = TruncateToBytes(text, maxBytes);
            
            return text;
        }

        static async Task DisconnectMqttAsync()
        {
            if (mqttClient != null && mqttConnected)
            {
                try
                {
                    await mqttClient.DisconnectAsync();
                    mqttClient.Dispose();
                    Console.WriteLine("   ✅ MQTT отключён");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ⚠️ MQTT ошибка: {ex.Message}");
                }
            }
        }
        
        static async Task ToggleMqttAsync()
        {
            if (config == null) return;
            
            if (mqttConnected)
            {
                await DisconnectMqttAsync();
                mqttConnected = false;
                config.Дополнительно.MqttEnabled = false;
                SaveConfig(config);
                Console.WriteLine("\n❌ MQTT ВЫКЛЮЧЁН");
            }
            else
            {
                config.Дополнительно.MqttEnabled = true;
                SaveConfig(config);
                await InitMqttAsync();
                if (mqttConnected)
                    Console.WriteLine("\n✅ MQTT ВКЛЮЧЁН");
                else
                    Console.WriteLine("\n⚠️ Не удалось подключить MQTT");
            }
        }
        
        static async Task RunNotificationMonitor()
        {
            try
            {
                listener = UserNotificationListener.Current;
                
                Console.WriteLine("🔐 Запрос доступа к уведомлениям...");
                var accessStatus = await listener.RequestAccessAsync();
                
                if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
                {
                    Console.WriteLine("\n❌ НЕТ ДОСТУПА К УВЕДОМЛЕНИЯМ!");
                    Console.WriteLine("\nДайте разрешение в Windows:");
                    Console.WriteLine("   Настройки → Конфиденциальность → Уведомления");
                    Console.WriteLine("\nРазрешите доступ для этого приложения и перезапустите.");
                    Console.WriteLine("\nНажмите любую клавишу для выхода...");
                    Console.ReadKey();
                    return;
                }
                
                Console.WriteLine("✅ ДОСТУП ПОЛУЧЕН\n");
                Console.WriteLine($"📊 Уже обработано: {processedIds.Count} уведомлений\n");
                Console.WriteLine("🔄 МОНИТОРИНГ АКТИВЕН (Ctrl+C = стоп)\n");
                
                startTime = DateTime.Now;
                mqttConnected = config != null && config.Дополнительно.MqttEnabled && config.MQTT.Enabled;
                if (config!.Дополнительно.EnableHotkeys)
                    _ = Task.Run(HandleKeyboard);
                
                int counter = 0;
                int statsCounter = 0;
                
                while (isRunning)
                {
                    await CheckNotifications();
                    counter++;
                    statsCounter++;
                    
                    int checkInterval = config.Мониторинг.CheckIntervalSeconds;
                    int statsInterval = config.Логирование.StatsLogIntervalMinutes * 60 / checkInterval;
                    
                    if (statsCounter >= statsInterval && statsInterval > 0)
                    {
                        var uptime = (DateTime.Now - startTime).TotalHours;
                        if (config.Логирование.LogToConsole)
                        {
                            Console.WriteLine($"\n📊 {uptime:F1}ч | Всего: {stats.Total} | Озвучено: {stats.Spoken} | Пропущено: {stats.Skipped}");
                        }
                        statsCounter = 0;
                    }
                    
                    await Task.Delay(checkInterval * 1000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ Ошибка: {ex.Message}");
                if (config!.Дополнительно.DebugMode)
                    Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                await Cleanup();
            }
        }

        static async Task CheckNotifications()
        {
            try
            {
                if (listener == null) return;
                
                var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                
                foreach (var notif in notifications)
                {
                    uint id = notif.Id;
                    
                    if (processedIds.Contains(id)) continue;
                    
                    string appName = notif.AppInfo.DisplayInfo.DisplayName;
                    if (string.IsNullOrEmpty(appName) || 
                        !appName.Contains("Telegram", StringComparison.OrdinalIgnoreCase))
                        continue;
                    
                    var (sender, message) = ParseNotification(notif);
                    
                    if (string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(message))
                        continue;
                    
                    await ProcessMessage(id, sender, message);
                }
            }
            catch (Exception ex)
            {
                if (config!.Дополнительно.DebugMode)
                    Console.WriteLine($"⚠️ Ошибка проверки: {ex.Message}");
            }
        }

        static (string? sender, string? message) ParseNotification(UserNotification notif)
        {
            try
            {
                var binding = notif.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding == null) return (null, null);
                
                var texts = binding.GetTextElements();
                if (texts == null || texts.Count < 2) return (null, null);
                
                string sender = texts[0]?.Text?.Trim() ?? "";
                string message = texts[1]?.Text?.Trim() ?? "";
                
                message = CleanMessage(message);
                
                return (sender, message);
            }
            catch
            {
                return (null, null);
            }
        }

        static async Task ProcessMessage(uint id, string sender, string message)
        {
            stats.Total++;
            
            if (config!.Мониторинг.ConsoleOutputEnabled)
            {
                Console.WriteLine($"\n📨 [{stats.Total}] {sender}");
                string shortMsg;
                if (config.Мониторинг.ConsoleMaxMessageLength > 0 && message.Length > config.Мониторинг.ConsoleMaxMessageLength)
                    shortMsg = message.Substring(0, config.Мониторинг.ConsoleMaxMessageLength);
                else
                    shortMsg = message;
                Console.WriteLine($"   {shortMsg}");
            }
            
            // Проверка черного списка отправителей
            if (IsBlacklistedSender(sender))
            {
                if (config.Мониторинг.ConsoleOutputEnabled)
                    Console.WriteLine($"   ⛔ Черный список");
                stats.Skipped++;
                processedIds.Add(id);
                return;
            }
            
            // Проверка разрешенных отправителей
            bool isAllowed = IsAllowedSender(sender);
            bool hasKeyword = HasKeywords(message);
            bool hasPriorityKeyword = HasPriorityKeywords(message);
            bool shouldSpeak = false;
            
            if (isAllowed)
            {
                if (config.ГолосовоеОповещение.SpeakOnlyOnKeywords)
                    shouldSpeak = hasKeyword;
                else
                    shouldSpeak = true;
            }
            
            if (shouldSpeak )//&& !isMuted)
            {
                string speakText = BuildSpeechText(sender, message);
                
                int minDelay = config.ГолосовоеОповещение.MinDelayBetweenMessagesMs;
				// Добавить тут
				if (!isMuted)//&& !isMuted)
				{
					if ((DateTime.Now - lastSpeechTime).TotalMilliseconds >= minDelay)
					{
						SpeakMessage(speakText, hasPriorityKeyword);
						lastSpeechTime = DateTime.Now;
						stats.Spoken++;
						
						if (config.Мониторинг.ConsoleOutputEnabled)
						{
							Console.WriteLine($"   🔊 ОЗВУЧЕНО!{(hasKeyword ? " 🔥 КЛЮЧЕВОЕ СЛОВО!" : "")}");
						}
						
						if (config.Мониторинг.ShowPopupNotifications)
						{
							ShowPopupNotification(sender, message);
						}
					}
					else if (config.Мониторинг.ConsoleOutputEnabled)
					{
						Console.WriteLine($"   ⏳ Задержка {minDelay}мс");
					}
				}	
				else 
				{
					if (config.Мониторинг.ConsoleOutputEnabled)
					{
						Console.WriteLine($"   🔇 Звук отключен (M)");
					}
				}	
				
                LogMessage(sender, message);
                
                // MQTT ОТПРАВКА
                await PublishToMqttAsync(sender, message);
            }
            else
            {
                stats.Skipped++;
                if (config.Мониторинг.ConsoleOutputEnabled)
                {
                    if (!isAllowed)
                        Console.WriteLine($"   ⏭️ Отправитель не в списке");
                    else if (isMuted)
                        Console.WriteLine($"   🔇 Звук отключен (M)");
                    else
                        Console.WriteLine($"   ⏭️ Нет ключевых слов");
                }
            }
            
            processedIds.Add(id);
            
            string deleteMode = config.УдалениеУведомлений.DeleteMode;
            if (deleteMode == "immediate")
            {
                try { listener?.RemoveNotification(id); }
                catch { }
                if (config.Мониторинг.ConsoleOutputEnabled)
                    Console.WriteLine($"   🗑️ Удалено");
            }
            else if (deleteMode == "on_exit")
            {
                pendingDeletes.Add(id);
                if (config.Мониторинг.ConsoleOutputEnabled && config.Дополнительно.DebugMode)
                    Console.WriteLine($"   📋 В очереди");
            }
            
            if (stats.Total % 10 == 0 && config.Мониторинг.SaveProcessedIds)
                SaveProcessedIds();
        }

        static bool IsAllowedSender(string sender)
        {
            var allowedSenders = config!.Фильтрация.AllowedSenders;
            if (allowedSenders.Count == 0) return true;
            
            bool partialMatch = config.Фильтрация.PartialMatchSenders;
            bool caseSensitive = config.Фильтрация.CaseSensitive;
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            
            foreach (var allowed in allowedSenders)
            {
                if (partialMatch)
                {
                    if (sender.Contains(allowed, comparison))
                        return true;
                }
                else
                {
                    if (string.Equals(sender, allowed, comparison))
                        return true;
                }
            }
            return false;
        }

        static bool IsBlacklistedSender(string sender)
        {
            var blacklisted = config!.Фильтрация.BlacklistedSenders;
            if (blacklisted.Count == 0) return false;
            
            bool partialMatch = config.Фильтрация.PartialMatchSenders;
            bool caseSensitive = config.Фильтрация.CaseSensitive;
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            
            foreach (var black in blacklisted)
            {
                if (partialMatch)
                {
                    if (sender.Contains(black, comparison))
                        return true;
                }
                else
                {
                    if (string.Equals(sender, black, comparison))
                        return true;
                }
            }
            return false;
        }

        static bool HasKeywords(string message)
        {
            var keywords = config!.Фильтрация.Keywords;
            if (keywords.Count == 0) return false;
            
            bool partialMatch = config.Фильтрация.PartialMatchKeywords;
            bool caseSensitive = config.Фильтрация.CaseSensitive;
            string msgToCheck = caseSensitive ? message : message.ToLower();
            
            foreach (var kw in keywords)
            {
                string keywordToCheck = caseSensitive ? kw : kw.ToLower();
                if (partialMatch)
                {
                    if (msgToCheck.Contains(keywordToCheck))
                        return true;
                }
                else
                {
                    if (msgToCheck == keywordToCheck)
                        return true;
                }
            }
            return false;
        }

        static bool HasPriorityKeywords(string message)
        {
            var priorityKeywords = config!.Приоритеты.PriorityKeywords;
            if (priorityKeywords.Count == 0) return false;
            
            string msgLower = message.ToLower();
            return priorityKeywords.Any(kw => msgLower.Contains(kw.ToLower()));
        }

        static string BuildSpeechText(string sender, string message)
        {
            var ttsConfig = config!.ГолосовоеОповещение;
            var parts = new List<string>();
            
            if (ttsConfig.SpeakTime)
            {
                string timeText = FormatTimeForSpeech();
                if (!string.IsNullOrEmpty(timeText))
                    parts.Add(timeText);
            }
            
            if (!string.IsNullOrEmpty(ttsConfig.PrefixText))
                parts.Add(ttsConfig.PrefixText);
            
            if (ttsConfig.SpeakFullMessage && !ttsConfig.SpeakOnlyTitle)
            {
                string msg = message;
                if (msg.Length > ttsConfig.MaxMessageLength)
                    msg = msg.Substring(0, ttsConfig.MaxMessageLength);
                parts.Add(NormalizeText(msg));
            }
            
            if (!string.IsNullOrEmpty(ttsConfig.SuffixText))
                parts.Add(ttsConfig.SuffixText);
            
            if (ttsConfig.SpeakSenderName)
                parts.Add(sender);
                                
            return string.Join(". ", parts);
        }

        static void SpeakMessage(string text, bool isPriority = false)
        {
            if (!config!.ГолосовоеОповещение.Enabled)
            {
                if (config.Звуки.EnableSounds)
                    PlaySound(config.Звуки.OnNewMessageSound);
                return;
            }
            
            if (tts == null)
            {
                if (config.ГолосовоеОповещение.FallbackBeepOnError)
                    Console.Beep(config.ГолосовоеОповещение.BeepFrequency, config.ГолосовоеОповещение.BeepDurationMs);
                return;
            }
            
            try
            {
                if (isPriority && config.Приоритеты.InterruptCurrentSpeech)
                {
                    tts.SpeakAsyncCancelAll();
                    if (config.Приоритеты.HighPriorityBeep)
                        Console.Beep(2000, 300);
                }
                
                if (config.ГолосовоеОповещение.AbortPreviousSpeech && tts.State == SynthesizerState.Speaking)
                    tts.SpeakAsyncCancelAll();
                
                tts.SpeakAsync(text);
                
                if (config.Звуки.EnableSounds)
                    PlaySound(isPriority ? config.Звуки.OnKeywordDetectedSound : config.Звуки.OnNewMessageSound);
            }
            catch (Exception ex)
            {
                if (config.Дополнительно.DebugMode)
                    Console.WriteLine($"   ⚠️ TTS ошибка: {ex.Message}");
                
                if (config.ГолосовоеОповещение.FallbackBeepOnError)
                    Console.Beep(config.ГолосовоеОповещение.BeepFrequency, config.ГолосовоеОповещение.BeepDurationMs);
            }
        }

        static void PlaySound(string soundType)
        {
            switch (soundType.ToLower())
            {
                case "beep":
                    Console.Beep(1000, 150);
                    break;
                case "beep_high":
                    Console.Beep(2000, 200);
                    break;
                case "beep_low":
                    Console.Beep(500, 300);
                    break;
            }
        }

        static void ShowPopupNotification(string sender, string message)
        {
            try
            {
                int timeout = config!.Мониторинг.PopupTimeoutSeconds;
                string shortMsg = message.Length > 50 ? message.Substring(0, 50) + "..." : message;
                Console.WriteLine($"   💬 [Уведомление] {sender}: {shortMsg}");
            }
            catch { }
        }

        static Config LoadConfig()
        {
            string configPath = "config.json";
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    var cfg = JsonConvert.DeserializeObject<Config>(json);
                    if (cfg != null)
                    {
                        SetConfigDefaults(cfg);
                        return cfg;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Ошибка загрузки конфига: {ex.Message}");
                }
            }
            
            var defaultConfig = GetDefaultConfig();
            SaveConfig(defaultConfig);
            return defaultConfig;
        }

        static void SetConfigDefaults(Config cfg)
        {
            if (cfg.ОчисткаТекста == null) cfg.ОчисткаТекста = new CleanTextConfig();
            if (cfg.Аббревиатуры == null) cfg.Аббревиатуры = new AbbrevConfig();
            if (cfg.Приоритеты == null) cfg.Приоритеты = new PriorityConfig();
            if (cfg.Аббревиатуры.CustomAbbreviations == null) cfg.Аббревиатуры.CustomAbbreviations = new Dictionary<string, string>();
            if (cfg.Фильтрация.BlacklistedSenders == null) cfg.Фильтрация.BlacklistedSenders = new List<string>();
            if (cfg.Фильтрация.BlacklistedKeywords == null) cfg.Фильтрация.BlacklistedKeywords = new List<string>();
            if (cfg.ОчисткаТекста.CustomAdsToRemove == null) cfg.ОчисткаТекста.CustomAdsToRemove = new List<string>();
            if (cfg.MQTT == null) cfg.MQTT = new MqttConfig();
            if (cfg.MQTT.MaxPayloadBytes == 0) cfg.MQTT.MaxPayloadBytes = 100;
        }

        static Config GetDefaultConfig()
        {
            return new Config
            {
                Мониторинг = new MonitorConfig
                {
                    CheckIntervalSeconds = 2,
                    MaxNotificationsPerCheck = 50,
                    StartMinimized = true,
                    ShowPopupNotifications = true,
                    PopupTimeoutSeconds = 5,
                    LogToFile = true,
                    LogFilePath = "telegram_alerts.log",
                    SaveProcessedIds = true,
                    ProcessedIdsFile = "processed.json",
                    MaxProcessedIdsCount = 1000,
                    ConsoleOutputEnabled = true,
                    ConsoleBeepOnMessage = true,
                    ConsoleMaxMessageLength = 0
                },
                Фильтрация = new FilterConfig
                {
                    AllowedSenders = new List<string> { },
                    RequireKeywords = false,
                    Keywords = new List<string> { },
                    BlacklistedSenders = new List<string>(),
                    BlacklistedKeywords = new List<string>(),
                    CaseSensitive = false,
                    PartialMatchSenders = true,
                    PartialMatchKeywords = true
                },
                ГолосовоеОповещение = new TtsConfig
                {
                    Enabled = true,
                    TtsEngine = "windows",
                    TtsRate = 1,
                    TtsVolume = 100,
                    MaxMessageLength = 300,
                    SpeakOnlyTitle = false,
                    SpeakSenderName = true,
                    SpeakFullMessage = true,
                    SpeakOnlyOnKeywords = false,
                    PrefixText = "",
                    SuffixText = "От канала",
                    FallbackBeepOnError = true,
                    BeepFrequency = 1500,
                    BeepDurationMs = 200,
                    AbortPreviousSpeech = true,
                    MinDelayBetweenMessagesMs = 500,
                    SpeakTime = false,
                    TimeFormat = "hours_minutes"
                },
                УдалениеУведомлений = new DeleteConfig
                {
                    DeleteMode = "on_exit",
                    DeleteOnlyProcessed = true,
                    DeleteAfterSeconds = 0,
                    KeepUnread = false
                },
                ОчисткаТекста = new CleanTextConfig
                {
                    RemoveAds = true,
                    CustomAdsToRemove = new List<string> { "Резервный канал", "подпишись", "Telegram" },
                    RemoveHtmlTags = true,
                    RemoveUrls = true,
                    RemoveEmojis = false,
                    RemoveExtraSpaces = true,
                    TrimMessage = true,
                    MaxMessageLength = 300
                },
                Аббревиатуры = new AbbrevConfig
                {
                    Enabled = true,
                    CustomAbbreviations = new Dictionary<string, string>
                    {
                        { "ФПВ", "Фи Пи Ви" },
                        { "БПЛА", "Бэ Пэ Эл А" },
                        { "РСЗО", "Эр Эс Зэ О" },
                        { "СВО", "Эс Вэ О" },
                        { "ВСУ", "Вэ Эс У" },
                        { "ЧС", "Чэ Эс" },
                        { "МЧС", "Эм Чэ Эс" },
                        { "ПВО", "Пэ Вэ О" },
						{ "МО", "Эм О" },
                        { "ЧП", "Чэ Пэ" }
                    }
                },
                Логирование = new LogConfig
                {
                    Enabled = true,
                    Separator = "--------------------------------------------------",
                    IncludeTimestamp = true,
                    TimestampFormat = "yyyy-MM-dd HH:mm:ss",
                    IncludeSender = true,
                    IncludeMessage = true,
                    IncludeStats = true,
                    StatsLogIntervalMinutes = 10,
                    MaxLogFileSizeMB = 10,
                    RotateLogs = true,
                    LogToConsole = true,
                    LogToFile = true
                },
                Звуки = new SoundConfig
                {
                    EnableSounds = true,
                    OnNewMessageSound = "beep",
                    OnKeywordDetectedSound = "beep_high",
                    OnErrorSound = "beep_low",
                    SoundVolume = 100,
                    CustomSoundWavPath = "",
                    CustomSoundKeywordWavPath = ""
                },
                Дополнительно = new ExtraConfig
                {
                    EnableHotkeys = true,
                    HotkeyStats = "S",
                    HotkeyConfig = "C",
                    HotkeyMute = "M",
                    HotkeyExit = "Escape",
                    AutoStartWithWindows = false,
                    RunInBackground = true,
                    ShowTrayIcon = false,
                    Language = "ru",
                    DebugMode = false,
                    DeveloperMode = false,
                    RememberVolume = true,
                    MqttEnabled = true
                },
                Приоритеты = new PriorityConfig
                {
                    PriorityKeywords = new List<string> { "ракета", "прилет", "взрыв" },
                    PriorityThreshold = 100,
                    InterruptCurrentSpeech = true,
                    HighPriorityBeep = true
                },
                MQTT = new MqttConfig
                {
                    Enabled = false,
                    TestMode = true,
                    Broker = "127.0.0.1",
                    Port = 1883,
                    Topic = "telegram/alerts",
                    ClientId = $"telegram_alert_{Environment.MachineName}",
                    IncludeTimestamp = true,
                    TimestampFormat = "HH:mm:ss",
                    FromNodeId = 1422149512,
                    ToNodeId = 4294967295,
                    ChannelIndex = 0,
                    MaxPayloadBytes = 100
                }
            };
        }

        static void SaveConfig(Config config)
        {
            try
            {
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText("config.json", json);
            }
            catch { }
        }

        static bool ParseArgs(string[] args)
        {
            if (args.Length == 0) return true;
            
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--help":
                    case "-h":
                        ShowHelp();
                        return false;
                    case "--test":
                        TestTTS();
                        return false;
                    case "--list":
                        ShowConfig();
                        return false;
                    case "--add-sender":
                        if (i + 1 < args.Length) AddSender(args[++i]);
                        return false;
                    case "--add-keyword":
                        if (i + 1 < args.Length) AddKeyword(args[++i]);
                        return false;
                    case "--delete-mode":
                        if (i + 1 < args.Length) SetDeleteMode(args[++i]);
                        return false;
                }
            }
            return true;
        }

        static void ShowHelp()
        {
            Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════╗
║         Telegram Alert Monitor - Уведомления Windows        ║
╚══════════════════════════════════════════════════════════════╝

ИСПОЛЬЗОВАНИЕ:
    TelegramAlert.exe [опции]

ОПЦИИ:
    --help               Показать справку
    --test               Тест голоса
    --list               Показать конфигурацию
    --add-sender <имя>   Добавить отправителя
    --add-keyword <слово> Добавить ключевое слово
    --delete-mode <mode> never/immediate/on_exit

ГОРЯЧИЕ КЛАВИШИ:
    Ctrl+C      - Остановка
    S           - Статистика
    C           - Конфигурация
    M           - Вкл/Выкл звук
    + / -       - Увеличение/уменьшение громкости TTS
    R           - Перезагрузка config.json без перезапуска
    Q           - Вкл/Выкл MQTT

MQTT НАСТРОЙКИ (config.json):
    - MQTT.Enabled          Вкл/Выкл отправку в MQTT
    - MQTT.Broker           Адрес MQTT сервера (например, 192.168.1.46)
    - MQTT.Port             Порт (по умолчанию 1883)
    - MQTT.Topic            Топик для публикации
    - MQTT.TestMode         Тестовый вывод без реальной отправки
    - MQTT.FromNodeId       ID ноды-шлюза (числовой)
    - MQTT.ToNodeId         ID получателя (4294967295 = всем)
    - MQTT.ChannelIndex     Индекс канала (0 = LongFast, 1 = Alerts)
    - MQTT.MaxPayloadBytes  Макс. размер сообщения в байтах (по умолч. 100)
");
        }

        static void ShowConfig()
        {
            if (config == null) return;
            
            Console.WriteLine("\n📋 ТЕКУЩАЯ КОНФИГУРАЦИЯ:");
            Console.WriteLine($"   Отправители ({config.Фильтрация.AllowedSenders.Count}):");
            foreach (var s in config.Фильтрация.AllowedSenders)
                Console.WriteLine($"      - {s}");
            Console.WriteLine($"   Ключевые слова: {string.Join(", ", config.Фильтрация.Keywords)}");
            Console.WriteLine($"   Требовать keywords: {config.Фильтрация.RequireKeywords}");
            Console.WriteLine($"   Режим удаления: {config.УдалениеУведомлений.DeleteMode}");
            Console.WriteLine($"   Интервал проверки: {config.Мониторинг.CheckIntervalSeconds}с");
            Console.WriteLine($"   TTS: {(config.ГолосовоеОповещение.Enabled ? "Вкл" : "Выкл")}");
            Console.WriteLine($"   Звук: {(isMuted ? "🔇 Выкл" : "🔊 Вкл")}");
            Console.WriteLine($"   MQTT: {(config.MQTT.Enabled ? (mqttConnected ? "✅ Подключён" : "⚠️ Отключён") : "🔇 Выкл")}");
        }

        static void AddSender(string sender)
        {
            if (!config!.Фильтрация.AllowedSenders.Contains(sender))
            {
                config.Фильтрация.AllowedSenders.Add(sender);
                SaveConfig(config);
                Console.WriteLine($"✅ Добавлен отправитель: {sender}");
            }
            else
            {
                Console.WriteLine($"❌ Отправитель уже существует: {sender}");
            }
        }

        static void AddKeyword(string keyword)
        {
            string lowerKeyword = keyword.ToLower();
            if (!config!.Фильтрация.Keywords.Contains(lowerKeyword))
            {
                config.Фильтрация.Keywords.Add(lowerKeyword);
                SaveConfig(config);
                Console.WriteLine($"✅ Добавлено ключевое слово: {keyword}");
            }
            else
            {
                Console.WriteLine($"❌ Ключевое слово уже существует: {keyword}");
            }
        }

        static void SetDeleteMode(string mode)
        {
            var valid = new[] { "never", "immediate", "on_exit" };
            if (valid.Contains(mode.ToLower()))
            {
                config!.УдалениеУведомлений.DeleteMode = mode.ToLower();
                SaveConfig(config);
                Console.WriteLine($"✅ Режим удаления: {mode}");
            }
            else
            {
                Console.WriteLine($"❌ Неверный режим. Доступно: {string.Join(", ", valid)}");
            }
        }

        static void TestTTS()
        {
            Console.WriteLine("🔊 Тест голоса...");
            using (var testTts = new SpeechSynthesizer())
            {
                testTts.SetOutputToDefaultAudioDevice();
                testTts.Speak("Проверка голосового оповещения. Если вы это слышите, всё работает.");
            }
            Console.WriteLine("✅ Тест завершён");
        }

        static void PrintBanner()
        {
            if (config == null) return;
            
            Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════╗
║        TELEGRAM ALERT MONITOR v5.25                          ║
║     Мониторинг через центр уведомлений Windows               ║
╚══════════════════════════════════════════════════════════════╝");
            Console.WriteLine($"   🕐 Запуск: {DateTime.Now:HH:mm:ss}");
            Console.WriteLine($"   📡 Интервал: {config.Мониторинг.CheckIntervalSeconds}с");
            Console.WriteLine($"   🗑️  Удаление: {config.УдалениеУведомлений.DeleteMode}");
            Console.WriteLine($"   📱 Каналов: {config.Фильтрация.AllowedSenders.Count}");
            Console.WriteLine($"   🔑 Ключевых слов: {config.Фильтрация.Keywords.Count}");
            Console.WriteLine($"   🔊 TTS: {(config.ГолосовоеОповещение.Enabled ? "Вкл" : "Выкл")}");
            Console.WriteLine($"   🔊 Громкость TTS: {stats.CurrentVolume}%");
            Console.WriteLine($"   📡 MQTT: {(config.MQTT.Enabled && config.Дополнительно.MqttEnabled ? (mqttConnected ? "Вкл" : "Ошибка") : "Выкл")}\n");
        }

        static void InitTTS()
        {
            if (!config!.ГолосовоеОповещение.Enabled)
            {
                Console.WriteLine("🔇 TTS отключен в настройках\n");
                tts = null;
                return;
            }
            
            try
            {
                tts = new SpeechSynthesizer();
                tts.SetOutputToDefaultAudioDevice();
                tts.Rate = config.ГолосовоеОповещение.TtsRate;
                tts.Volume = config.ГолосовоеОповещение.TtsVolume;
                stats.CurrentVolume = config.ГолосовоеОповещение.TtsVolume;
                
                foreach (var voice in tts.GetInstalledVoices())
                {
                    if (voice.VoiceInfo.Culture.Name.StartsWith("ru"))
                    {
                        tts.SelectVoice(voice.VoiceInfo.Name);
                        Console.WriteLine($"🎤 Голос: {voice.VoiceInfo.Name}\n");
                        break;
                    }
                }
                
                if (tts.Voice == null)
                    Console.WriteLine("🎤 Используется голос по умолчанию\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ TTS ошибка: {ex.Message}");
                Console.WriteLine("   Будет использован звуковой сигнал\n");
                tts = null;
            }
        }

        static void LoadProcessedIds()
        {
            if (!config!.Мониторинг.SaveProcessedIds) return;
            
            try
            {
                string filePath = config.Мониторинг.ProcessedIdsFile;
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    var ids = JsonConvert.DeserializeObject<uint[]>(json);
                    if (ids != null)
                        processedIds = new HashSet<uint>(ids);
                    Console.WriteLine($"📊 Загружено ID: {processedIds.Count}\n");
                }
            }
            catch { }
        }

        static void SaveProcessedIds()
        {
            if (!config!.Мониторинг.SaveProcessedIds) return;
            
            try
            {
                if (processedIds.Count > config.Мониторинг.MaxProcessedIdsCount)
                {
                    var lastIds = processedIds.TakeLast(config.Мониторинг.MaxProcessedIdsCount / 2).ToArray();
                    processedIds = new HashSet<uint>(lastIds);
                }
                
                string filePath = config.Мониторинг.ProcessedIdsFile;
                File.WriteAllText(filePath, JsonConvert.SerializeObject(processedIds.ToArray()));
            }
            catch { }
        }

        static string CleanMessage(string text)
        {
            if (config!.ОчисткаТекста.RemoveAds)
            {
                var ads = config.ОчисткаТекста.CustomAdsToRemove;
                foreach (var ad in ads)
                    text = text.Replace(ad, "");
            }
            
            if (config.ОчисткаТекста.RemoveHtmlTags)
                text = Regex.Replace(text, "<.*?>", "");
            
            if (config.ОчисткаТекста.RemoveUrls)
                text = Regex.Replace(text, @"https?:\/\/[^\s]+", "");
            
            if (config.ОчисткаТекста.RemoveEmojis)
                text = Regex.Replace(text, @"[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u26FF]", "");
            
            if (config.ОчисткаТекста.RemoveExtraSpaces)
                text = Regex.Replace(text, @"\s+", " ");
            
            if (config.ОчисткаТекста.TrimMessage)
                text = text.Trim();
            
            if (text.Length > config.ОчисткаТекста.MaxMessageLength)
                text = text.Substring(0, config.ОчисткаТекста.MaxMessageLength);
            
            return text;
        }

        static string NormalizeText(string text)
        {
            if (!config!.Аббревиатуры.Enabled)
                return text;
            
            var abbrevs = config.Аббревиатуры.CustomAbbreviations;
            foreach (var kv in abbrevs)
            {
                text = Regex.Replace(text, $@"\b{Regex.Escape(kv.Key)}\b", kv.Value, RegexOptions.IgnoreCase);
            }
            
            return text;
        }
        
        static string FormatTimeForSpeech()
        {
            var now = DateTime.Now;
            var format = config!.ГолосовоеОповещение.TimeFormat;
            
            if (format == "none") return "";
            
            if (format == "hours_minutes")
            {
                int hours = now.Hour;
                int minutes = now.Minute;
                
                string hoursWord = GetHourWord(hours);
                string minutesWord = GetMinuteWord(minutes);
                
                return $"{hours} {hoursWord}, {minutes} {minutesWord}.";
            }
            
            if (format == "minutes_only")
            {
                return now.ToString("HH") + " часов " + now.ToString("mm") + " минут.";
            }
            
            return "";
        }

        static string GetHourWord(int hours)
        {
            if (hours % 10 == 1 && hours != 11) return "час";
            if (hours % 10 >= 2 && hours % 10 <= 4 && (hours < 12 || hours > 14)) return "часа";
            return "часов";
        }

        static string GetMinuteWord(int minutes)
        {
            if (minutes % 10 == 1 && minutes != 11) return "минута";
            if (minutes % 10 >= 2 && minutes % 10 <= 4 && (minutes < 12 || minutes > 14)) return "минуты";
            return "минут";
        }
        
        static void LogMessage(string sender, string message)
        {
            if (!config!.Логирование.Enabled || !config.Логирование.LogToFile) return;
            
            try
            {
                string logFile = config.Мониторинг.LogFilePath;
                
                if (config.Логирование.RotateLogs)
                {
                    var fi = new FileInfo(logFile);
                    if (fi.Exists && fi.Length > config.Логирование.MaxLogFileSizeMB * 1024 * 1024)
                    {
                        string backup = logFile + ".bak";
                        if (File.Exists(backup)) File.Delete(backup);
                        File.Move(logFile, backup);
                    }
                }
                
                var parts = new List<string>();
                if (config.Логирование.IncludeTimestamp)
                    parts.Add($"[{DateTime.Now.ToString(config.Логирование.TimestampFormat)}]");
                if (config.Логирование.IncludeSender)
                    parts.Add(sender);
                if (config.Логирование.IncludeMessage)
                    parts.Add(message);
                
                string logLine = string.Join(": ", parts);
                File.AppendAllText(logFile, logLine + "\n" + config.Логирование.Separator + "\n");
            }
            catch { }
        }

        static async void HandleKeyboard()
        {
            while (isRunning)
            {
                var key = Console.ReadKey(true);
                
                string statsKey = config!.Дополнительно.HotkeyStats.ToUpper();
                string configKey = config.Дополнительно.HotkeyConfig.ToUpper();
                string muteKey = config.Дополнительно.HotkeyMute.ToUpper();
                
                if (key.Key.ToString() == statsKey)
                {
                    var uptime = DateTime.Now - startTime;
                    Console.WriteLine($"\n📊 СТАТИСТИКА:");
                    Console.WriteLine($"   Всего сообщений: {stats.Total}");
                    Console.WriteLine($"   Озвучено: {stats.Spoken}");
                    Console.WriteLine($"   MQTT отправлено: {stats.MqttSent}");
                    Console.WriteLine($"   Пропущено: {stats.Skipped}");
                    Console.WriteLine($"   Время работы: {uptime:hh\\:mm\\:ss}");
                    Console.WriteLine($"   Звук: {(isMuted ? "🔇 Выкл" : "🔊 Вкл")}");
                    Console.WriteLine($"   MQTT: {(mqttConnected ? "✅ Подключён" : "❌ Отключён")}");
                }
                else if (key.Key.ToString() == configKey)
                {
                    ShowConfig();
                }
                else if (key.Key == ConsoleKey.OemPlus || key.Key == ConsoleKey.Add)
                {
                    int newVolume = stats.CurrentVolume + 10;
                    if (newVolume > 100) newVolume = 100;
                    
                    stats.CurrentVolume = newVolume;
                    if (tts != null) tts.Volume = newVolume;
                    
                    Console.Beep(1500, 100);
                    Console.WriteLine($"\n🔊 Громкость: {newVolume}%");
                    
                    if (config!.Дополнительно.RememberVolume)
                    {
                        config.ГолосовоеОповещение.TtsVolume = newVolume;
                        SaveConfig(config);
                    }
                }
                else if (key.Key == ConsoleKey.OemMinus || key.Key == ConsoleKey.Subtract)
                {
                    int newVolume = stats.CurrentVolume - 10;
                    if (newVolume < 0) newVolume = 0;
                    
                    stats.CurrentVolume = newVolume;
                    if (tts != null) tts.Volume = newVolume;
                    
                    Console.Beep(1000, 100);
                    Console.WriteLine($"\n🔊 Громкость: {newVolume}%");
                    
                    if (config!.Дополнительно.RememberVolume)
                    {
                        config.ГолосовоеОповещение.TtsVolume = newVolume;
                        SaveConfig(config);
                    }
                }
                else if (key.Key == ConsoleKey.R)
                {
                    Console.WriteLine($"\n🔄 Перезагрузка конфигурации...");
                    
                    try
                    {
                        if (config!.Дополнительно.RememberVolume && tts != null)
                        {
                            config.ГолосовоеОповещение.TtsVolume = tts.Volume;
                        }
                        
                        var newConfig = LoadConfig();
                        
                        if (tts != null)
                        {
                            tts.Rate = newConfig.ГолосовоеОповещение.TtsRate;
                            tts.Volume = newConfig.ГолосовоеОповещение.TtsVolume;
                            stats.CurrentVolume = newConfig.ГолосовоеОповещение.TtsVolume;
                        }
                        
                        oldConfig = config;
                        config = newConfig;
                        
                        Console.WriteLine($"   ✅ Конфиг перезагружен");
                        Console.WriteLine($"   🔇 Mute: {(isMuted ? "Вкл" : "Выкл")}");
                        Console.WriteLine($"   🔊 Громкость: {stats.CurrentVolume}%");
                        
                        Console.Beep(1000, 100);
                        Console.Beep(1200, 100);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   ❌ Ошибка: {ex.Message}");
                        Console.Beep(500, 500);
                    }
                }
                else if (key.Key == ConsoleKey.Q)
                {
                    await ToggleMqttAsync();
                }
                else if (key.Key.ToString() == muteKey)
                {
                    isMuted = !isMuted;
                    Console.WriteLine($"\n🔇 Звук {(isMuted ? "ВЫКЛЮЧЕН" : "ВКЛЮЧЕН")}");
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    isRunning = false;
                    break;
                }
            }
        }

        static async Task Cleanup()
        {
            isRunning = false;
            
            Console.WriteLine("\n🧹 Остановка и очистка...");
            
            await DisconnectMqttAsync();
            
            if (config!.УдалениеУведомлений.DeleteMode == "on_exit" && pendingDeletes.Count > 0)
            {
                Console.WriteLine($"   🧹 Очистка {pendingDeletes.Count} уведомлений...");
                int deleted = 0;
                foreach (var id in pendingDeletes)
                {
                    try 
                    { 
                        listener?.RemoveNotification(id); 
                        deleted++; 
                    }
                    catch { }
                }
                Console.WriteLine($"   ✅ Удалено: {deleted}");
            }
            
            SaveProcessedIds();
            tts?.Dispose();
            
            var totalTime = DateTime.Now - startTime;
            Console.WriteLine("\n📊 ИТОГОВАЯ СТАТИСТИКА:");
            Console.WriteLine($"   Всего сообщений: {stats.Total}");
            Console.WriteLine($"   Озвучено: {stats.Spoken}");
            Console.WriteLine($"   Пропущено: {stats.Skipped}");
            Console.WriteLine($"   Время работы: {totalTime:hh\\:mm\\:ss}");
            Console.WriteLine("\n✅ Завершено. Логи в " + config.Мониторинг.LogFilePath);
        }

        // ==================== КЛАССЫ КОНФИГУРАЦИИ ====================

        public class Config
        {
            public MonitorConfig Мониторинг { get; set; } = new();
            public FilterConfig Фильтрация { get; set; } = new();
            public TtsConfig ГолосовоеОповещение { get; set; } = new();
            public DeleteConfig УдалениеУведомлений { get; set; } = new();
            public CleanTextConfig ОчисткаТекста { get; set; } = new();
            public AbbrevConfig Аббревиатуры { get; set; } = new();
            public LogConfig Логирование { get; set; } = new();
            public SoundConfig Звуки { get; set; } = new();
            public ExtraConfig Дополнительно { get; set; } = new();
            public PriorityConfig Приоритеты { get; set; } = new();
            public MqttConfig MQTT { get; set; } = new();
        }

        public class MonitorConfig
        {
            public int CheckIntervalSeconds { get; set; } = 2;
            public int MaxNotificationsPerCheck { get; set; } = 50;
            public bool StartMinimized { get; set; } = true;
            public bool ShowPopupNotifications { get; set; } = true;
            public int PopupTimeoutSeconds { get; set; } = 5;
            public bool LogToFile { get; set; } = true;
            public string LogFilePath { get; set; } = "telegram_alerts.log";
            public bool SaveProcessedIds { get; set; } = true;
            public string ProcessedIdsFile { get; set; } = "processed.json";
            public int MaxProcessedIdsCount { get; set; } = 1000;
            public bool ConsoleOutputEnabled { get; set; } = true;
            public int ConsoleMaxMessageLength { get; set; } = 0;
            public bool ConsoleBeepOnMessage { get; set; } = true;
        }

        public class FilterConfig
        {
            public List<string> AllowedSenders { get; set; } = new();
            public bool RequireKeywords { get; set; } = false;
            public List<string> Keywords { get; set; } = new();
            public List<string> BlacklistedSenders { get; set; } = new();
            public List<string> BlacklistedKeywords { get; set; } = new();
            public bool CaseSensitive { get; set; } = false;
            public bool PartialMatchSenders { get; set; } = true;
            public bool PartialMatchKeywords { get; set; } = true;
        }

        public class TtsConfig
        {
            public bool Enabled { get; set; } = true;
            public string TtsEngine { get; set; } = "windows";
            public int TtsRate { get; set; } = 1;
            public int TtsVolume { get; set; } = 100;
            public int MaxMessageLength { get; set; } = 300;
            public bool SpeakOnlyTitle { get; set; } = false;
            public bool SpeakSenderName { get; set; } = true;
            public bool SpeakFullMessage { get; set; } = true;
            public bool SpeakOnlyOnKeywords { get; set; } = false;
            public bool SpeakTime { get; set; } = false;
            public string TimeFormat { get; set; } = "hours_minutes";
            public string PrefixText { get; set; } = "Внимание";
            public string SuffixText { get; set; } = "";
            public bool FallbackBeepOnError { get; set; } = true;
            public int BeepFrequency { get; set; } = 1500;
            public int BeepDurationMs { get; set; } = 200;
            public bool AbortPreviousSpeech { get; set; } = true;
            public int MinDelayBetweenMessagesMs { get; set; } = 500;
        }

        public class DeleteConfig
        {
            public string DeleteMode { get; set; } = "on_exit";
            public bool DeleteOnlyProcessed { get; set; } = true;
            public int DeleteAfterSeconds { get; set; } = 0;
            public bool KeepUnread { get; set; } = false;
        }

        public class CleanTextConfig
        {
            public bool RemoveAds { get; set; } = true;
            public List<string> CustomAdsToRemove { get; set; } = new();
            public bool RemoveHtmlTags { get; set; } = true;
            public bool RemoveUrls { get; set; } = true;
            public bool RemoveEmojis { get; set; } = false;
            public bool RemoveExtraSpaces { get; set; } = true;
            public bool TrimMessage { get; set; } = true;
            public int MaxMessageLength { get; set; } = 300;
        }

        public class AbbrevConfig
        {
            public bool Enabled { get; set; } = true;
            public Dictionary<string, string> CustomAbbreviations { get; set; } = new();
        }

        public class LogConfig
        {
            public bool Enabled { get; set; } = true;
            public string Separator { get; set; } = "--------------------------------------------------";
            public bool IncludeTimestamp { get; set; } = true;
            public string TimestampFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";
            public bool IncludeSender { get; set; } = true;
            public bool IncludeMessage { get; set; } = true;
            public bool IncludeStats { get; set; } = true;
            public int StatsLogIntervalMinutes { get; set; } = 10;
            public int MaxLogFileSizeMB { get; set; } = 10;
            public bool RotateLogs { get; set; } = true;
            public bool LogToConsole { get; set; } = true;
            public bool LogToFile { get; set; } = true;
        }

        public class SoundConfig
        {
            public bool EnableSounds { get; set; } = true;
            public string OnNewMessageSound { get; set; } = "beep";
            public string OnKeywordDetectedSound { get; set; } = "beep_high";
            public string OnErrorSound { get; set; } = "beep_low";
            public int SoundVolume { get; set; } = 100;
            public string CustomSoundWavPath { get; set; } = "";
            public string CustomSoundKeywordWavPath { get; set; } = "";
        }

        public class ExtraConfig
        {
            public bool EnableHotkeys { get; set; } = true;
            public string HotkeyStats { get; set; } = "S";
            public string HotkeyConfig { get; set; } = "C";
            public string HotkeyMute { get; set; } = "M";
            public string HotkeyExit { get; set; } = "Escape";
            public bool AutoStartWithWindows { get; set; } = false;
            public bool RunInBackground { get; set; } = true;
            public bool ShowTrayIcon { get; set; } = false;
            public string Language { get; set; } = "ru";
            public bool DebugMode { get; set; } = false;
            public bool DeveloperMode { get; set; } = false;
            public bool RememberVolume { get; set; } = true;
            public bool MqttEnabled { get; set; } = true;
        }

        public class PriorityConfig
        {
            public List<string> PriorityKeywords { get; set; } = new();
            public int PriorityThreshold { get; set; } = 100;
            public bool InterruptCurrentSpeech { get; set; } = true;
            public bool HighPriorityBeep { get; set; } = true;
        }

        public class MqttConfig
        {
            public bool Enabled { get; set; } = false;
            public bool TestMode { get; set; } = false;
            public string Broker { get; set; } = "127.0.0.1";
            public int Port { get; set; } = 1883;
            public string Topic { get; set; } = "telegram/alerts";
            public string ClientId { get; set; } = $"telegram_alert_{Environment.MachineName}";
            public uint FromNodeId { get; set; } = 2130636288;
            public uint ToNodeId { get; set; } = 4294967295;
            public int ChannelIndex { get; set; } = 0;
            public bool IncludeTimestamp { get; set; } = true;
            public string TimestampFormat { get; set; } = "HH:mm:ss";
            public int MaxPayloadBytes { get; set; } = 100;
        }

        public class Stats
        {
            public int Total { get; set; } = 0;
            public int Spoken { get; set; } = 0;
            public int Skipped { get; set; } = 0;
            public int CurrentVolume { get; set; } = 50;
            public int MqttSent { get; set; } = 0;
        }
    }
}