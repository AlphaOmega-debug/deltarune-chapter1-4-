using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace RrPingBot
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            string? botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
            if (string.IsNullOrWhiteSpace(botToken))
            {
                Console.Error.WriteLine("Environment variable TELEGRAM_BOT_TOKEN is not set.");
                Console.Error.WriteLine("Export TELEGRAM_BOT_TOKEN=<your_token> and run again.");
                return;
            }

            var botClient = new TelegramBotClient(botToken);

            using var cts = new CancellationTokenSource();

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };

            botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                pollingErrorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: cts.Token);

            var me = await botClient.GetMeAsync(cts.Token);
            Console.WriteLine($"Bot @{me.Username} is up. Send /ping to get current Rush Royale ping.");
            Console.WriteLine("Press Ctrl+C to exit.");

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cts.Token);
            }
            catch (TaskCanceledException)
            {
                // normal shutdown
            }
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken cancellationToken)
        {
            if (update.Type != UpdateType.Message || update.Message is null)
                return;

            var message = update.Message;
            if (message.Text is null)
                return;

            var text = message.Text.Trim();
            if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
            {
                await bot.SendTextMessageAsync(chatId: message.Chat.Id,
                    text: "Send /ping to get the current Rush Royale ping from isdown.io.",
                    cancellationToken: cancellationToken);
                return;
            }

            if (text.Equals("/ping", StringComparison.OrdinalIgnoreCase))
            {
                await bot.SendChatActionAsync(message.Chat.Id, ChatAction.Typing, cancellationToken: cancellationToken);
                var result = await TryGetPingAsync();

                var reply = result.Success
                    ? $"Rush Royale ping: {result.PingMs} ms"
                    : $"Could not fetch ping right now. {result.ErrorMessage}";

                await bot.SendTextMessageAsync(chatId: message.Chat.Id,
                    text: reply,
                    replyToMessageId: message.MessageId,
                    cancellationToken: cancellationToken);
                return;
            }
        }

        private static Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken cancellationToken)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}] {apiRequestException.Message}",
                _ => exception.ToString()
            };

            Console.Error.WriteLine(errorMessage);
            return Task.CompletedTask;
        }

        private sealed record PingResult(bool Success, int PingMs, string? ErrorMessage);

        private static async Task<PingResult> TryGetPingAsync()
        {
            try
            {
                using var playwright = await Playwright.CreateAsync();
                await EnsureBrowserInstalledOnceAsync();

                await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true
                });

                var context = await browser.NewContextAsync();
                var page = await context.NewPageAsync();

                // Go to the page and wait for DOM to be ready
                await page.GotoAsync("https://isdown.io/rr", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30000
                });

                // Wait for the #cping element to contain digits like "12 ms"
                await page.WaitForSelectorAsync("#cping", new PageWaitForSelectorOptions { Timeout = 15000 });

                // Give the page script a moment to populate and animate
                await page.WaitForFunctionAsync(
                    "() => { const el = document.querySelector('#cping'); return !!el && /\\d+\\s*ms/.test(el.textContent || ''); }");

                var cpingText = await page.InnerTextAsync("#cping");

                var match = Regex.Match(cpingText, "(\\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var pingMs))
                {
                    return new PingResult(true, pingMs, null);
                }

                return new PingResult(false, 0, $"Unexpected content: '{cpingText}'");
            }
            catch (TimeoutException tex)
            {
                return new PingResult(false, 0, $"Timeout: {tex.Message}");
            }
            catch (Exception ex)
            {
                return new PingResult(false, 0, ex.Message);
            }
        }

        private static bool _browserEnsured;
        private static readonly SemaphoreSlim BrowserInstallLock = new(1, 1);

        private static async Task EnsureBrowserInstalledOnceAsync()
        {
            if (_browserEnsured) return;
            await BrowserInstallLock.WaitAsync();
            try
            {
                if (_browserEnsured) return;

                // Ensure Playwright browsers are installed for this runtime
                try
                {
                    // This invokes the embedded Playwright driver to install browsers
                    _ = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: playwright install failed: {ex.Message}");
                }

                _browserEnsured = true;
            }
            finally
            {
                BrowserInstallLock.Release();
            }
        }
    }
}
