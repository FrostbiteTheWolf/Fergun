﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using Discord.Addons.CommandCache;
using Discord.Addons.Interactive;
using Discord.Commands;
using Discord.WebSocket;
using Fergun.APIs;
using Fergun.APIs.DuckDuckGo;
using Fergun.APIs.UrbanDictionary;
using Fergun.Attributes;
using Fergun.Attributes.Preconditions;
using Fergun.Extensions;
using Fergun.Responses;
using Fergun.Services;
using GoogleTranslateFreeApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using org.mariuszgromada.math.mxparser;
using YoutubeExplode;

namespace Fergun.Modules
{
    [RequireBotPermission(Constants.MinimunRequiredPermissions)]
    [Ratelimit(3, Constants.GlobalRatelimitPeriod, Measure.Minutes)]
    public class Utility : FergunBase
    {
        [ThreadStatic]
        private static Random _rngInstance;

        // A regex i copied and pasted from somewhere (yep)
        private static readonly Regex _linkParser = new Regex(@"^(http:\/\/www\.|https:\/\/www\.|http:\/\/|https:\/\/)?[a-z0-9]+([\-\.]{1}[a-z0-9]+)*\.[a-z]{2,5}(:[0-9]{1,5})?(\/.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _bracketReplacer = new Regex(@"\[(.+?)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled); // \[(\[*.+?]*)\]
        private static readonly HttpClient _deepAiClient = new HttpClient();
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly YoutubeClient _ytClient = new YoutubeClient();
        private static bool _isCreatingCache;
        private static XkcdResponse _lastComic;
        private static DateTimeOffset _timeToCheckComic;
        private static CommandService _cmdService;
        private static LogService _logService;

        //public static TesseractEngine TessEngine { get; } = new TesseractEngine("./tessdata", "eng", EngineMode.LstmOnly);
        public static ConcurrentBag<CachedPages> ImgCache { get; } = new ConcurrentBag<CachedPages>();
        public static ConcurrentBag<CachedPages> UrbanCache { get; } = new ConcurrentBag<CachedPages>();
        public static ConcurrentBag<string> VideoCache { get; } = new ConcurrentBag<string>();
        private static Random RngInstance => _rngInstance ??= new Random();

        public Utility(CommandService commands, LogService logService)
        {
            _cmdService ??= commands;
            _logService ??= logService;
        }

        [Command("avatar")]
        [Summary("avatarSummary")]
        [Example("Fergun#6839")]
        public async Task Avatar([Remainder, Summary("avatarParam1")] IUser user = null)
        {
            user ??= Context.User;

            // Prevent getting error 404 while downloading the avatar getting the user from REST.
            var restUser = await Context.Client.Rest.GetUserAsync(user.Id);

            string avatarUrl = restUser.GetAvatarUrl(Discord.ImageFormat.Auto, 2048) ?? restUser.GetDefaultAvatarUrl();
            string thumbnail = restUser.GetAvatarUrl(Discord.ImageFormat.Png, 128) ?? restUser.GetDefaultAvatarUrl();

            System.Drawing.Color avatarColor;
            using (Stream response = await _httpClient.GetStreamAsync(new Uri(thumbnail)))
            using (Bitmap img = new Bitmap(response))
            {
                avatarColor = img.GetAverageColor();
            }

            var builder = new EmbedBuilder
            {
                Title = user.ToString(),
                ImageUrl = avatarUrl,
                Color = new Discord.Color(avatarColor.R, avatarColor.G, avatarColor.B)
            };

            await ReplyAsync(embed: builder.Build());
        }

        [LongRunning]
        [Command("badtranslator", RunMode = RunMode.Async), Ratelimit(1, Constants.GlobalRatelimitPeriod, Measure.Minutes)]
        [Summary("badtranslatorSummary")]
        [Alias("bt")]
        [Example("i don't know what to say lol")]
        public async Task<RuntimeResult> Badtranslator([Remainder, Summary("badtranslatorParam1")] string text = null)
        {
            var builder = new EmbedBuilder
            {
                Title = "Bad translator"
            };

            if (text == null)
            {
                if (FergunClient.WordList.Count == 0)
                {
                    return FergunResult.FromError("Could not get the word list.");
                }

                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", "Badtranslator: No text passed. Obtaining text from the word list."));
                // Get random words
                int maxLength = RngInstance.Next(6, 10);
                for (int i = 0; i < maxLength; i++)
                {
                    text += FergunClient.WordList[RngInstance.Next(0, FergunClient.WordList.Count)] + ' ';
                }
                builder.AddField(Locate("Input"), text);
            }
            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Badtranslator: Text to use: {text}"));

            List<string> languageChain = new List<string>();
            int chainCount = 7;
            string originalLang = null;
            SimpleTranslationResult result;

            for (int i = 0; i < chainCount; i++)
            {
                string targetLang;
                if (i == chainCount - 1)
                {
                    targetLang = originalLang;
                }
                else
                {
                    // Get unique and random languages.
                    do
                    {
                        targetLang = Translators.SupportedLanguages[RngInstance.Next(Translators.SupportedLanguages.Count)];
                    } while (languageChain.Contains(targetLang));
                }

                result = await TranslateSimpleAsync(text, targetLang, "");
                if (result.Error != null)
                {
                    return FergunResult.FromError(Locate(result.Error));
                }
                if (i == 0)
                {
                    originalLang = result.Source.ISO639;
                    await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Badtranslator: Original language: {originalLang}"));

                    // Fallback to English if the detected language is not supported by Bing.
                    if (Translators.SupportedLanguages.IndexOf(originalLang) == -1)
                    {
                        await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", "Badtranslator: Original language not supported by Bing. Fallback to English."));
                        originalLang = "en";
                    }
                    languageChain.Add(originalLang);
                }
                text = result.Text;
                languageChain.Add(targetLang);
            }

            if (text.Length > EmbedFieldBuilder.MaxFieldValueLength)
            {
                try
                {
                    var response = await Hastebin.UploadAsync(text);
                    text = Format.Url(Locate("HastebinLink"), response.GetLink());
                }
                catch (HttpRequestException e)
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", "Paste: Error while uploading text to Hastebin", e));
                    text = text.Truncate(EmbedFieldBuilder.MaxFieldValueLength);
                }
            }

            builder.AddField(Locate("LanguageChain"), string.Join(" -> ", languageChain))
                .AddField(Locate("Result"), text)
                .WithThumbnailUrl("https://fergun.is-inside.me/gXEDLZVr.png")
                .WithColor(FergunConfig.EmbedColor);

            await ReplyAsync(embed: builder.Build());

            return FergunResult.FromSuccess();
        }

        [Command("base64decode")]
        [Summary("base64decodeSummary")]
        [Alias("b64decode", "b64d")]
        [Example("aGVsbG8gd29ybGQ=")]
        public async Task<RuntimeResult> Base64decode([Remainder, Summary("base64decodeParam1")] string text)
        {
            if (!text.IsBase64())
            {
                return FergunResult.FromError(Locate("base64decodeInvalid"));
            }

            await ReplyAsync(Encoding.UTF8.GetString(Convert.FromBase64String(text)), allowedMentions: AllowedMentions.None);
            return FergunResult.FromSuccess();
        }

        [Command("base64encode")]
        [Summary("base64encodeSummary")]
        [Alias("b64encode", "b64e")]
        [Example("hello")]
        public async Task Base64encode([Remainder, Summary("base64encodeParam1")] string text)
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            if (encoded.Length > DiscordConfig.MaxMessageSize)
            {
                try
                {
                    var response = await Hastebin.UploadAsync(encoded);
                    await ReplyAsync(response.GetLink());
                }
                catch (HttpRequestException e)
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", "Paste: Error while uploading text to Hastebin", e));
                    await ReplyAsync(encoded.Truncate(DiscordConfig.MaxMessageSize));
                }
            }
            else
            {
                await ReplyAsync(encoded);
            }
        }

        [Command("bigeditsnipe", RunMode = RunMode.Async)]
        [Summary("bigeditsnipeSummary")]
        [Alias("besnipe", "bes")]
        public async Task<RuntimeResult> Bigeditsnipe([Summary("bigeditsnipeParam1")] IMessageChannel channel = null)
        {
            channel ??= Context.Channel;
            var msgs = FergunClient.MessageCache
                .Where(x => x.SourceEvent == SourceEvent.MessageUpdated && x.Channel.Id == channel.Id)
                .OrderByDescending(x => x.CachedAt)
                .Take(20);

            var builder = new EmbedBuilder();
            if (!msgs.Any())
            {
                builder.WithDescription(string.Format(Locate("NothingToSnipe"), MentionUtils.MentionChannel(channel.Id)));
            }
            else
            {
                var text = msgs.Select(x =>
                {
                    return $"{Format.Bold(x.Author.ToString())} ({string.Format(Locate("MinutesAgo"), (DateTimeOffset.UtcNow - x.CreatedAt).Minutes)})\n{x.Content.Truncate(200)}\n\n";
                });

                builder.WithTitle("Big edit snipe")
                    .WithDescription(string.Concat(text).Truncate(EmbedBuilder.MaxDescriptionLength))
                    .WithFooter($"{Locate("In")} #{channel.Name}");
            }
            builder.WithColor(FergunConfig.EmbedColor);

            await ReplyAsync(embed: builder.Build());
            return FergunResult.FromSuccess();
        }

        [Command("bigsnipe", RunMode = RunMode.Async)]
        [Summary("bigsnipeSummary")]
        [Alias("bsnipe", "bs")]
        public async Task<RuntimeResult> Bigsnipe([Summary("bigsnipeParam1")] IMessageChannel channel = null)
        {
            channel ??= Context.Channel;
            var msgs = FergunClient.MessageCache
                .Where(x => x.SourceEvent == SourceEvent.MessageDeleted && x.Channel.Id == channel.Id)
                .OrderByDescending(x => x.CachedAt)
                .Take(20);

            var builder = new EmbedBuilder();
            if (!msgs.Any())
            {
                builder.WithDescription(string.Format(Locate("NothingToSnipe"), MentionUtils.MentionChannel(channel.Id)));
            }
            else
            {
                string text = "";
                foreach (var msg in msgs)
                {
                    text += $"{Format.Bold(msg.Author.ToString())} ({string.Format(Locate("MinutesAgo"), (DateTimeOffset.UtcNow - msg.CreatedAt).Minutes)})\n";
                    text += !string.IsNullOrEmpty(msg.Content) ? msg.Content.Truncate(200) : msg.Attachments.Count > 0 ? $"({Locate("Attachment")})" : "?";
                    text += "\n\n";
                }
                builder.WithTitle("Big snipe")
                    .WithDescription(text.Truncate(EmbedBuilder.MaxDescriptionLength))
                    .WithFooter($"{Locate("In")} #{channel.Name}");
            }
            builder.WithColor(FergunConfig.EmbedColor);

            await ReplyAsync(embed: builder.Build());
            return FergunResult.FromSuccess();
        }

        [Command("calc", RunMode = RunMode.Async)]
        [Summary("calcSummary")]
        [Alias("calculate")]
        [Example("2 * 2 - 1")]
        public async Task<RuntimeResult> Calc([Remainder, Summary("calcParam1")] string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return FergunResult.FromError(Locate("InvalidExpression"));
            }
            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Calc: expression: {expression}"));

            Stopwatch sw = Stopwatch.StartNew();
            Expression ex = new Expression(expression);
            //if (!ex.checkSyntax())
            //{
            //    return FergunResult.FromError(ex.getErrorMessage());
            //}
            string result = ex.calculate().ToString();
            sw.Stop();

            if (result.Length > EmbedFieldBuilder.MaxFieldValueLength)
            {
                result = result.Truncate(EmbedFieldBuilder.MaxFieldValueLength - 3) + "...";
            }

            var builder = new EmbedBuilder()
                    .WithTitle(Locate("CalcResults"))
                    .AddField(Locate("Input"), Format.Code(expression.Truncate(EmbedFieldBuilder.MaxFieldValueLength - 10), "md"))
                    .AddField(Locate("Output"), Format.Code(result.Truncate(EmbedFieldBuilder.MaxFieldValueLength - 10), "md"))
                    .WithFooter(string.Format(Locate("EvalFooter"), sw.ElapsedMilliseconds))
                    .WithColor(FergunConfig.EmbedColor);

            await ReplyAsync(embed: builder.Build());

            return FergunResult.FromSuccess();
        }

        [Command("channelinfo")]
        [Summary("channelinfoSummary")]
        [Alias("channel")]
        [Example("#general")]
        public async Task<RuntimeResult> Channelinfo([Remainder, Summary("channelinfoParam1")] IChannel channel = null)
        {
            channel ??= Context.Channel;

            var builder = new EmbedBuilder()
                .WithTitle(Locate("ChannelInfo"))
                .AddField(Locate("Name"), channel.Name, true)
                .AddField("ID", channel.Id, true);

            if (channel is ITextChannel textChannel)
            {
                builder.AddField(Locate("Type"), Locate(channel is SocketNewsChannel ? "AnnouncementChannel" : "TextChannel"), true)
                    .AddField(Locate("Topic"), string.IsNullOrEmpty(textChannel.Topic) ? Locate("None") : textChannel.Topic, true)
                    .AddField(Locate("IsNSFW"), Locate(textChannel.IsNsfw), true)
                    .AddField(Locate("SlowMode"), TimeSpan.FromSeconds(channel is SocketNewsChannel ? 0 : textChannel.SlowModeInterval).ToShortForm2(), true)
                    .AddField(Locate("Category"), textChannel.CategoryId.HasValue ? Context.Guild.GetCategoryChannel(textChannel.CategoryId.Value).Name : Locate("None"), true);
            }
            else if (channel is IVoiceChannel voiceChannel)
            {
                builder.AddField(Locate("Type"), Locate("VoiceChannel"), true)
                    .AddField(Locate("Bitrate"), $"{voiceChannel.Bitrate / 1000}kbps", true)
                    .AddField(Locate("UserLimit"), voiceChannel.UserLimit?.ToString() ?? Locate("NoLimit"), true);
            }
            else if (channel is SocketCategoryChannel categoryChannel)
            {
                builder.AddField(Locate("Type"), Locate("Category"), true)
                    .AddField(Locate("Channels"), categoryChannel.Channels.Count, true);
            }
            else if (channel is IDMChannel)
            {
                builder.AddField(Locate("Type"), Locate("DMChannel"), true);
            }
            if (channel is IGuildChannel guildChannel)
            {
                builder.AddField(Locate("Position"), guildChannel.Position, true);
            }
            builder.AddField(Locate("CreatedAt"), channel.CreatedAt, true)
                .AddField(Locate("Mention"), MentionUtils.MentionChannel(channel.Id), true)
                .WithColor(FergunConfig.EmbedColor);

            await ReplyAsync(embed: builder.Build());

            return FergunResult.FromSuccess();
        }

        [Command("choice")]
        [Summary("choiceSummary")]
        [Alias("choose")]
        [Example("c c++ c#")]
        public async Task<RuntimeResult> Choice([Summary("choiceParam1")] params string[] choices)
        {
            if (choices.Length == 0)
            {
                return FergunResult.FromError(Locate("NoChoices"));
            }
            await ReplyAsync($"{Locate("IChoose")} **{choices[RngInstance.Next(0, choices.Length)]}**{(choices.Length == 1 ? Locate("OneChoice") : "")}", allowedMentions: AllowedMentions.None);
            return FergunResult.FromSuccess();
        }

        [Command("color")]
        [Summary("colorSummary")]
        [Example("#ff983e")]
        public async Task<RuntimeResult> Color([Summary("colorParam1")] string color = null)
        {
            System.Drawing.Color _color;
            if (string.IsNullOrWhiteSpace(color))
            {
                _color = System.Drawing.Color.FromArgb(RngInstance.Next(0, 256), RngInstance.Next(0, 256), RngInstance.Next(0, 256));
                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Color: Generated random color: {_color}"));
            }
            else
            {
                color = color.TrimStart('#');
                if (!int.TryParse(color, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rawColor))
                {
                    if (!int.TryParse(color, NumberStyles.Integer, CultureInfo.InvariantCulture, out rawColor))
                    {
                        rawColor = System.Drawing.Color.FromName(color).ToArgb();
                        if (rawColor == 0)
                        {
                            rawColor = color.ToColor();
                            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Color: Converted string to color: {rawColor}"));
                            //rawColor = uint.Parse(color.ToColor(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        }
                        //return FergunResult.FromError(Locate("InvalidColor"));
                    }
                }
                _color = System.Drawing.Color.FromArgb(rawColor);
                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Color: {rawColor} -> {_color}"));
            }

            using (Bitmap bmp = new Bitmap(500, 500))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.FromArgb(_color.R, _color.G, _color.B));
                }
                using (Stream stream = new MemoryStream())
                {
                    bmp.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    stream.Position = 0;
                    var msg = await Context.Channel.SendCachedFileAsync(Cache, Context.Message.Id, stream, $"{_color}.png");
                }
            }

            return FergunResult.FromSuccess();
        }

        [AlwaysEnabled]
        [RequireContext(ContextType.Guild, ErrorMessage = "NotSupportedInDM")]
        [RequireUserPermission(GuildPermission.ManageGuild, ErrorMessage = "UserRequireManageServer")]
        [LongRunning]
        [Command("config", RunMode = RunMode.Async)]
        [Summary("configSummary")]
        [Alias("configuration", "settings")]
        public async Task<RuntimeResult> Config()
        {
            var guild = GetGuildConfig() ?? new GuildConfig(Context.Guild.Id);

            string listToShow = "";
            string[] configList = Locate("ConfigList").Split(Environment.NewLine, StringSplitOptions.None);
            for (int i = 0; i < configList.Length; i++)
            {
                listToShow += $"**{i + 1}.** {configList[i]}\n";
            }
            //bool hasReacted = false;
            IUserMessage message = null;

            string valueList =
                $"{Locate(guild.CaptionbotAutoTranslate)}\n" +
                $"{Locate(guild.AidAutoTranslate)}\n" +
                $"{Locate(guild.TrackSelection)}";

            var builder = new EmbedBuilder()
                .WithAuthor(Context.User)
                .WithTitle(Locate("FergunConfig"))
                .WithDescription(Locate("ConfigPrompt"))
                .AddField(Locate("Option"), listToShow, true)
                .AddField(Locate("Value"), valueList, true)
                .WithColor(FergunConfig.EmbedColor);

            async Task HandleReactionAsync(SocketReaction reaction)
            {
                FergunClient.Database.UpdateRecord("Guilds", guild);
                valueList =
                    $"{Locate(guild.CaptionbotAutoTranslate)}\n" +
                    $"{Locate(guild.AidAutoTranslate)}\n" +
                    $"{Locate(guild.TrackSelection)}";

                builder.Fields[1] = new EmbedFieldBuilder { Name = Locate("Value"), Value = valueList, IsInline = true };
                _ = message.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
                await message.ModifyAsync(x => x.Embed = builder.Build());
            }
            ReactionCallbackData data = new ReactionCallbackData(null, builder.Build(), false, false, TimeSpan.FromMinutes(2))
                .AddCallBack(new Emoji("1️⃣"), async (_, reaction) =>
                {
                    //hasReacted = true;
                    guild.CaptionbotAutoTranslate = !guild.CaptionbotAutoTranslate;
                    await HandleReactionAsync(reaction);
                })
                .AddCallBack(new Emoji("2️⃣"), async (_, reaction) =>
                {
                    guild.AidAutoTranslate = !guild.AidAutoTranslate;
                    await HandleReactionAsync(reaction);
                    //hasReacted = true;
                })
                .AddCallBack(new Emoji("3️⃣"), async (_, reaction) =>
                {
                    guild.TrackSelection = !guild.TrackSelection;
                    await HandleReactionAsync(reaction);
                    //hasReacted = true;
                })
                .AddCallBack(new Emoji("❌"), async (_, reaction) =>
                {
                    await message.TryDeleteAsync();
                });

            message = await InlineReactionReplyAsync(data);

            return FergunResult.FromSuccess();
        }

        [Command("editsnipe", RunMode = RunMode.Async)]
        [Summary("editsnipeSummary")]
        [Alias("esnipe")]
        [Example("#bots")]
        public async Task Editsnipe([Summary("snipeParam1")] IMessageChannel channel = null)
        {
            channel ??= Context.Channel;
            var message = FergunClient.MessageCache
                .Where(x => x.SourceEvent == SourceEvent.MessageUpdated && x.Channel.Id == channel.Id)
                .OrderByDescending(x => x.CachedAt)
                .FirstOrDefault();

            var builder = new EmbedBuilder();
            if (message == null)
            {
                builder.WithDescription(string.Format(Locate("NothingToSnipe"), MentionUtils.MentionChannel(channel.Id)));
            }
            else
            {
                builder.WithAuthor(message.Author)
                    .WithDescription(message.Content.Truncate(EmbedBuilder.MaxDescriptionLength))
                    .WithFooter($"{Locate("In")} #{message.Channel.Name}")
                    .WithTimestamp(message.CreatedAt);
            }
            builder.WithColor(FergunConfig.EmbedColor);

            await ReplyAsync(embed: builder.Build());
        }

        [AlwaysEnabled]
        [Command("help")]
        [Summary("helpSummary")]
        [Alias("ayuda", "yardım")]
        [Example("help")]
        public async Task<RuntimeResult> Help([Remainder, Summary("helpParam1")] string commandName = null)
        {
            var builder = new EmbedBuilder();
            if (commandName == null)
            {
                var textCommands = _cmdService.Commands.Where(x => x.Module.Name == "Text").Select(x => x.Name);
                var utilityCommands = _cmdService.Commands.Where(x => x.Module.Name == "Utility").Select(x => x.Name);
                var moderationCommands = _cmdService.Commands.Where(x => x.Module.Name == "Moderation").Select(x => x.Name);
                var musicCommands = _cmdService.Commands.Where(x => x.Module.Name == "Music").Select(x => x.Name);
                var aidCommands = _cmdService.Commands.Where(x => x.Module.Name == "aid").Select(x => x.Name);
                var otherCommands = _cmdService.Commands.Where(x => x.Module.Name == "Other").Select(x => x.Name);
                var ownerCommands = _cmdService.Commands.Where(x => x.Module.Name == "Owner").Select(x => x.Name);
                int visibleCommandCount = _cmdService.Commands.Count(x => x.Module.Name != Constants.DevelopmentModuleName);

                builder.WithTitle(Locate("CommandList"))
                    .AddField(Locate("TextCommands"), string.Join(", ", textCommands))
                    .AddField(Locate("UtilityCommands"), string.Join(", ", utilityCommands))
                    .AddField(Locate("ModerationCommands"), string.Join(", ", moderationCommands))
                    //.AddField(GetValue("EntertainmentCommands"), "trivia, ...")
                    .AddField(Locate("MusicCommands"), string.Join(", ", musicCommands))
                    .AddField(string.Format(Locate("AIDCommands"), GetPrefix()), string.Join(", ", aidCommands))
                    .AddField(Locate("OtherCommands"), string.Join(", ", otherCommands))
                    .AddField(Locate("OwnerCommands"), string.Join(", ", ownerCommands))
                    .AddField(Locate("Notes"), string.Format(Locate("NotesInfo"), GetPrefix()));

                string version = $"v{Constants.Version}";
                if (FergunClient.IsDebugMode)
                {
                    version += "-dev";
                }
                else
                {
                    builder.AddField("Links",
                        string.Format(Locate("Links"),
                        FergunClient.InviteLink,
                        FergunClient.DblBotPage,
                        $"{FergunClient.DblBotPage}/vote",
                        FergunConfig.SupportServer));
                }
                builder.WithFooter(string.Format(Locate("HelpFooter"), version, visibleCommandCount))
                    .WithColor(FergunConfig.EmbedColor);

                await ReplyAsync(embed: builder.Build());
            }
            else
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Help: Getting help for command: {commandName}"));
                var command = _cmdService.Commands.FirstOrDefault(x => x.Aliases.Any(y => y == commandName.ToLowerInvariant()) && x.Module.Name != Constants.DevelopmentModuleName);
                if (command == null)
                {
                    return FergunResult.FromError(string.Format(Locate("CommandNotFound"), GetPrefix()));
                }
                var embed = command.ToHelpEmbed(GetLanguage(), GetPrefix());
                await ReplyAsync(embed: embed);
            }
            return FergunResult.FromSuccess();
        }

        [LongRunning]
        [Command("identify", RunMode = RunMode.Async)]
        [Summary("identifySummary")]
        [Alias("captionbot")]
        [Remarks("NoUrlPassed")]
        [Example("https://www.fergun.com/image.png")]
        public async Task<RuntimeResult> Identify([Summary("identifyParam1")] string url = null)
        {
            string error;
            (url, error) = await GetLastUrlAsync(Constants.ClientConfig.MessageCacheSize, true, url);
            if (url == null)
            {
                return FergunResult.FromError(error);
            }
            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Identify: url to use: {url}"));

            var data = new Dictionary<string, string>
            {
                { "Content", url },
                { "Type", "CaptionRequest" }
            };

            string text;
            try
            {
                using (var content = new FormUrlEncodedContent(data))
                {
                    var response = await _httpClient.PostAsync(new Uri("https://captionbot.azurewebsites.net/api/messages?language=en-US"), content);
                    response.EnsureSuccessStatusCode();
                    text = await response.Content.ReadAsStringAsync();
                }
            }
            catch (HttpRequestException e)
            {
                return FergunResult.FromError(e.Message);
            }
            text = text.Trim('\"');

            bool autoTranslate = GetGuildConfig()?.CaptionbotAutoTranslate ?? FergunConfig.CaptionbotAutoTranslateDefault;
            if (autoTranslate && GetLanguage() != "en")
            {
                var result = await TranslateSimpleAsync(text, GetLanguage(), "en");
                if (result.Error == null)
                {
                    text = result.Text;
                }
            }

            await ReplyAsync(text);
            return FergunResult.FromSuccess();
        }

        [LongRunning]
        [Command("img", RunMode = RunMode.Async), Ratelimit(1, Constants.GlobalRatelimitPeriod, Measure.Minutes)]
        [Summary("imgSummary")]
        [Alias("im", "image", "ddgi")]
        [Example("discord")]
        public async Task<RuntimeResult> Img([Remainder, Summary("imgParam1")] string query)
        {
            query = query.Trim();

            // Considering a DM channel a SFW channel.
            bool isNsfwChannel = !Context.IsPrivate && (Context.Channel as ITextChannel).IsNsfw;
            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Img: Query \"{query}\", NSFW channel: {isNsfwChannel}"));

            var pages = new List<PaginatedMessage.Page>();

            var cached = ImgCache.FirstOrDefault(x => x.Query == query && x.IsNsfw == isNsfwChannel);
            if (cached == null)
            {
                DdgResponse search;
                try
                {
                    search = await DdgApi.SearchImagesAsync(query, !isNsfwChannel ? SafeSearch.Strict : SafeSearch.Off);
                }
                catch (HttpRequestException e)
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", "Error searching images", e));
                    return FergunResult.FromError(Locate("AnErrorOccurred"));
                }
                catch (TokenNotFoundException e)
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", "Error searching images", e));
                    return FergunResult.FromError(e.Message);
                }
                if (search.Results.Count == 0)
                {
                    return FergunResult.FromError(Locate("NoResultsFound"));
                }
                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Results count: {search.Results.Count}"));

                foreach (var item in search.Results)
                {
                    string imageUrl = Uri.EscapeUriString(Uri.UnescapeDataString(item.Image));
                    if (!Uri.IsWellFormedUriString(imageUrl, UriKind.Absolute))
                    {
                        await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Image: Invalid image url: {imageUrl}"));
                    }
                    else if (!Uri.IsWellFormedUriString(item.Url, UriKind.Absolute))
                    {
                        await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Image: Invalid url: {item.Url}"));
                    }
                    else
                    {
                        pages.Add(new PaginatedMessage.Page()
                        {
                            Title = item.Title.Truncate(EmbedBuilder.MaxTitleLength),
                            ImageUrl = imageUrl,
                            Url = item.Url
                        });
                    }
                }
                ImgCache.Add(new CachedPages(query, pages, isNsfwChannel));
            }
            else
            {
                pages = cached.Pages;
            }
            var pager = new PaginatedMessage()
            {
                Author = new EmbedAuthorBuilder()
                {
                    Name = Context.User.ToString(),
                    IconUrl = Context.User.GetAvatarUrl() ?? Context.User.GetDefaultAvatarUrl()
                },
                Description = Locate("ImageSearch"),
                Pages = pages,
                Color = new Discord.Color(FergunConfig.EmbedColor),
                Options = new PaginatedAppearanceOptions()
                {
                    InformationText = Locate("PaginatorHelp"),
                    FooterFormat = Locate("PaginatorFooter"),
                    Timeout = TimeSpan.FromMinutes(10),
                    ActionOnTimeout = ActionOnTimeout.DeleteReactions
                }
            };

            var reactions = new ReactionList()
            {
                First = pages.Count >= 3,
                Backward = true,
                Forward = true,
                Last = pages.Count >= 3,
                Stop = true,
                Jump = pages.Count >= 4,
                Info = false
            };

            await PagedReplyAsync(pager, reactions);

            return FergunResult.FromSuccess();
        }

        //[LongRunning]
        [RequireBotPermission(ChannelPermission.AttachFiles, ErrorMessage = "BotRequireAttachFiles")]
        [Command("invert", RunMode = RunMode.Async), Ratelimit(2, Constants.GlobalRatelimitPeriod, Measure.Minutes)]
        [Summary("invertSummary")]
        [Alias("negate", "negative")]
        [Example("https://www.fergun.com/image.png")]
        public async Task<RuntimeResult> Invert([Remainder, Summary("invertParam1")] string url = null)
        {
            string error;
            (url, error) = await GetLastUrlAsync(Constants.ClientConfig.MessageCacheSize, true, url);
            if (url == null)
            {
                return FergunResult.FromError(error);
            }
            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Invert: url to use: {url}"));

            using (Stream response = await _httpClient.GetStreamAsync(new Uri(url)))
            using (Bitmap img = new Bitmap(response))
            using (Bitmap inverted = img.InvertColor())
            using (Stream invertedFile = new MemoryStream())
            {
                System.Drawing.Imaging.ImageFormat format = inverted.RawFormat;
                if (inverted.RawFormat.Guid == System.Drawing.Imaging.ImageFormat.MemoryBmp.Guid)
                {
                    format = System.Drawing.Imaging.ImageFormat.Jpeg;
                }
                inverted.Save(invertedFile, format);
                if (invertedFile.Length > Constants.AttachmentSizeLimit)
                {
                    return FergunResult.FromError("The file is too large.");
                }
                invertedFile.Position = 0;
                await Context.Channel.SendCachedFileAsync(Cache, Context.Message.Id, invertedFile, $"invert{format.FileExtensionFromEncoder()}");
            }

            return FergunResult.FromSuccess();
        }

        [Command("lmgtfy", RunMode = RunMode.Async)]
        [Summary("lmgtfySummary")]
        public async Task Google([Remainder, Summary("lmgtfyParam1")] string query)
        {
            await ReplyAsync($"https://lmgtfy.com/?q={Uri.EscapeDataString(query)}", allowedMentions: AllowedMentions.None);
        }

        [LongRunning]
        [Command("ocr", RunMode = RunMode.Async), Ratelimit(2, 1, Measure.Minutes)]
        [Summary("ocrSummary")]
        [Remarks("NoUrlPassed")]
        [Example("https://www.fergun.com/image.png")]
        public async Task<RuntimeResult> Ocr([Summary("ocrParam1")] string url = null)
        {
            string error;
            (url, error) = await GetLastUrlAsync(Constants.ClientConfig.MessageCacheSize, true, url);
            if (url == null)
            {
                return FergunResult.FromError(error);
            }
            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Ocr: url to use: {url}"));

            var result = await OcrSimpleAsync(url);
            if (!int.TryParse(result.Item1, out int processTime))
            {
                return FergunResult.FromError(Locate(result.Item1));
            }
            string text = result.Item2;

            if (text.Length > EmbedFieldBuilder.MaxFieldValueLength - 10)
            {
                try
                {
                    var response = await Hastebin.UploadAsync(text);
                    text = Format.Url(Locate("HastebinLink"), response.GetLink());
                }
                catch (HttpRequestException e)
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", "Paste: Error while uploading text to Hastebin", e));
                    text = Format.Code(text.Truncate(EmbedFieldBuilder.MaxFieldValueLength - 10), "md");
                }
            }
            else
            {
                text = Format.Code(text.Truncate(EmbedFieldBuilder.MaxFieldValueLength - 10), "md");
            }

            var builder = new EmbedBuilder()
                    .WithTitle(Locate("OcrResults"))
                    .AddField(Locate("Output"), text)
                    .WithFooter(string.Format(Locate("ProcessingTime"), processTime))
                    .WithColor(FergunConfig.EmbedColor);

            await ReplyAsync(embed: builder.Build());

            return FergunResult.FromSuccess();
        }

        [LongRunning]
        [Command("ocrtranslate", RunMode = RunMode.Async), Ratelimit(2, 1, Measure.Minutes)]
        [Summary("ocrtranslateSummary")]
        [Alias("ocrtr")]
        [Remarks("NoUrlPassed")]
        [Example("en https://www.fergun.com/image.png")]
        public async Task<RuntimeResult> Ocrtr([Summary("ocrtranslateParam1")] string target,
            [Summary("ocrtranslateParam2")] string url = null)
        {
            if (!GoogleTranslator.IsLanguageSupported(new Language("", target)))
            {
                return FergunResult.FromError($"{Locate("InvalidLanguage")}\n{string.Join(" ", Translators.SupportedLanguages.Select(x => Format.Code(x)))}");
            }

            string error;
            (url, error) = await GetLastUrlAsync(Constants.ClientConfig.MessageCacheSize, true, url);
            if (url == null)
            {
                return FergunResult.FromError(error);
            }
            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Orctranslate: url to use: {url}"));

            var ocrResult = await OcrSimpleAsync(url);
            if (!int.TryParse(ocrResult.Item1, out int processTime))
            {
                return FergunResult.FromError(Locate(ocrResult.Item1));
            }
            string text = ocrResult.Item2;

            var sw = Stopwatch.StartNew();
            var result = await TranslateSimpleAsync(text, target);
            sw.Stop();
            if (result.Error != null)
            {
                return FergunResult.FromError(Locate(result.Error));
            }

            if (result.Text.Length > EmbedFieldBuilder.MaxFieldValueLength - 10)
            {
                try
                {
                    var response = await Hastebin.UploadAsync(result.Text);
                    result.Text = Format.Url(Locate("HastebinLink"), response.GetLink());
                }
                catch (HttpRequestException e)
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", "Paste: Error while uploading text to Hastebin", e));
                    result.Text = Format.Code(result.Text.Truncate(EmbedFieldBuilder.MaxFieldValueLength - 10), "md");
                }
            }
            else
            {
                result.Text = Format.Code(result.Text.Truncate(EmbedFieldBuilder.MaxFieldValueLength - 10), "md");
            }

            var builder = new EmbedBuilder()
                .WithTitle(Locate("OcrtrResults"))
                .AddField(Locate("Input"), Format.Code(text.Truncate(EmbedFieldBuilder.MaxFieldValueLength - 10), "md"))
                .AddField(Locate("SourceLanguage"), result.Source?.FullName ?? "???", false)
                .AddField(Locate("TargetLanguage"), result.Target.FullName, false)
                .AddField(Locate("Result"), result.Text, false)
                .WithFooter(string.Format(Locate("ProcessingTime"), processTime + sw.ElapsedMilliseconds))
                .WithColor(FergunConfig.EmbedColor);

            await ReplyAsync(embed: builder.Build());

            return FergunResult.FromSuccess();
        }

        //[LongRunning]
        [Command("paste", RunMode = RunMode.Async), Ratelimit(1, Constants.GlobalRatelimitPeriod, Measure.Minutes)]
        [Summary("pasteSummary")]
        [Alias("haste")]
        public async Task<RuntimeResult> Paste([Remainder, Summary("pasteParam1")] string text)
        {
            await SendEmbedAsync($"{Constants.LoadingEmote} {Locate("Uploading")}");
            try
            {
                await SendEmbedAsync(Format.Url(Locate("HastebinLink"), (await Hastebin.UploadAsync(text)).GetLink()));
            }
            catch (HttpRequestException e)
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", "Paste: Error while uploading text to Hastebin", e));
                return FergunResult.FromError(e.Message);
            }
            return FergunResult.FromSuccess();
        }

        [Command("ping")]
        [Summary("pingSummary")]
        public async Task Ping()
        {
            var sw = Stopwatch.StartNew();
            await SendEmbedAsync(Format.Bold("Pong!"));
            sw.Stop();

            var sw2 = Stopwatch.StartNew();
            FergunClient.Database.Find<GuildConfig>("Guilds", _ => true);
            sw2.Stop();

            await SendEmbedAsync($"⏱{Format.Bold("Message")}: {sw.ElapsedMilliseconds}ms\n\n" +
                $"{Constants.WebSocketEmote}{Format.Bold("WebSocket")}: {Context.Client.Latency}ms\n\n" +
                $"{Constants.MongoDbEmote}{Format.Bold("Database")}: {Math.Round(sw2.Elapsed.TotalMilliseconds, 2)}ms");
        }

        [LongRunning]
        [Command("resize", RunMode = RunMode.Async), Ratelimit(1, Constants.GlobalRatelimitPeriod, Measure.Minutes)]
        [Summary("resizeSummary")]
        [Alias("waifu2x", "w2x")]
        [Remarks("NoUrlPassed")]
        [Example("https://www.fergun.com/image.png")]
        public async Task<RuntimeResult> Resize([Summary("resizeParam1")] string url = null)
        {
            string error;
            (url, error) = await GetLastUrlAsync(Constants.ClientConfig.MessageCacheSize, true, url);
            if (url == null)
            {
                return FergunResult.FromError(error);
            }
            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Resize: url to use: {url}"));

            var data = new Dictionary<string, string>
            {
                { "image", url }
            };

            var content = new FormUrlEncodedContent(data);
            string responseString;

            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepai.org/api/waifu2x"))
            {
                request.Headers.Add("Api-Key", FergunConfig.DeepAiApiKey);
                request.Content = content;
                var response = await _deepAiClient.SendAsync(request);
                responseString = await response.Content.ReadAsStringAsync();
            }

            JToken token = JObject.Parse(responseString);
            string resultUrl = token.Value<string>("output_url");
            if (string.IsNullOrWhiteSpace(resultUrl))
            {
                return FergunResult.FromError(Locate("AnErrorOccurred"));
            }

            var builder = new EmbedBuilder()
                .WithTitle(Locate("ResizeResults"))
                .WithImageUrl(resultUrl)
                .WithColor(FergunConfig.EmbedColor);

            await ReplyAsync(embed: builder.Build());
            return FergunResult.FromSuccess();
        }

        [RequireContext(ContextType.Guild, ErrorMessage = "NotSupportedInDM")]
        [Command("roleinfo")]
        [Summary("roleinfoSummary")]
        [Alias("role")]
        [Example("Devs")]
        public async Task<RuntimeResult> Roleinfo([Remainder, Summary("roleinfoParam1")] SocketRole role)
        {
            var builder = new EmbedBuilder()
                .WithTitle(Locate("RoleInfo"))

                .AddField(Locate("Name"), role.Name, true)
                .AddField(Locate("Color"), $"{role.Color} ({role.Color.R}, {role.Color.G}, {role.Color.B})", true)
                .AddField(Locate("IsMentionable"), Locate(role.IsMentionable), true)

                .AddField("ID", role.Id, true)
                .AddField(Locate("IsHoisted"), Locate(role.IsHoisted), true)
                .AddField(Locate("Position"), role.Position, true)

                .AddField(Locate("Permissions"), role.Permissions.RawValue == 0 ? Locate("None") : Format.Code(string.Join("`, `", role.Permissions.ToList())), false)

                .AddField(Locate("MemberCount"), role.Members.Count(), true)
                .AddField(Locate("CreatedAt"), role.CreatedAt, true)
                .AddField(Locate("Mention"), role.Mention, true)

                .WithColor(role.Color);

            await ReplyAsync(embed: builder.Build());

            return FergunResult.FromSuccess();
        }

        // The attribute order matters
        [RequireNsfw(ErrorMessage = "NSFWOnly")]
        [RequireBotPermission(ChannelPermission.AttachFiles, ErrorMessage = "BotRequireAttachFiles")]
        [LongRunning]
        [Command("screenshot", RunMode = RunMode.Async), Ratelimit(2, 1, Measure.Minutes)]
        [Summary("screenshotSummary")]
        [Alias("ss")]
        [Example("https://www.fergun.com")]
        public async Task<RuntimeResult> Screenshot([Summary("screenshotParam1")] string url)
        {
            Uri uri;
            try
            {
                uri = new UriBuilder(Uri.UnescapeDataString(url)).Uri;
            }
            catch (UriFormatException)
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Screenshot: Invalid url: {Uri.UnescapeDataString(url)}"));
                return FergunResult.FromError(Locate("InvalidUrl"));
            }
            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Screenshot: Url: {uri.AbsoluteUri}"));

            ApiFlashResponse response;
            try
            {
                response = await ApiFlash.UrlToImageAsync(FergunConfig.ApiFlashAccessKey, uri.AbsoluteUri, ApiFlash.FormatType.png, "400,403,404,500-511");
            }
            catch (ArgumentException e)
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", $"Screenshot: Error in UrlToImageAsync", e));
                return FergunResult.FromError(Locate("InvalidUrl"));
            }
            catch (WebException e)
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", $"Screenshot: Error in UrlToImageAsync", e));
                return FergunResult.FromError(e.Message);
            }

            if (response.ErrorMessage != null)
            {
                return FergunResult.FromError(response.ErrorMessage);
            }

            //var data = new Dictionary<string, string>
            //{
            //    { "image", Response.Url }
            //};

            //string responseString;

            //using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepai.org/api/nsfw-detector"))
            //{
            //    request.Headers.Add("Api-Key", Config.DeepAiApiKey);
            //    request.Content = new FormUrlEncodedContent(data);
            //    var response = await DeepAIClient.SendAsync(request);
            //    responseString = await response.Content.ReadAsStringAsync();
            //}

            //JToken token2 = JObject.Parse(responseString);
            //double score = (double)token2["output"]["nsfw_score"];
            //if (score > 0.3 && !(Context.Channel as SocketTextChannel).IsNsfw)
            //{
            //    await ReplyAsync(GetValue("ScreenshotNSFW"));
            //    return;
            //}

            using (Stream image = await _httpClient.GetStreamAsync(new Uri(response.Url)))
            {
                await Context.Channel.SendCachedFileAsync(Cache, Context.Message.Id, image, "screenshot.png");
            }
            return FergunResult.FromSuccess();
        }

        [Command("serverinfo", RunMode = RunMode.Async)]
        [Summary("serverinfoSummary")]
        [Alias("server", "guild", "guildinfo")]
        public async Task<RuntimeResult> Serverinfo(string serverId = null)
        {
            SocketGuild server;
            if (Context.User.Id == (await Context.Client.GetApplicationInfoAsync()).Owner.Id)
            {
                server = serverId == null ? Context.Guild : Context.Client.GetGuild(ulong.Parse(serverId));
                if (server == null)
                {
                    return FergunResult.FromError(Locate("GuildNotFound"));
                }
            }
            else
            {
                server = Context.Guild;
            }

            var builder = new EmbedBuilder()
                .WithTitle(Locate("ServerInfo"))

                .AddField(Locate("Name"), server.Name, true)
                .AddField(Locate("Owner"), MentionUtils.MentionUser(server.OwnerId), true)
                .AddField("ID", server.Id, true)

                .AddField(Locate("CategoryCount"), server.CategoryChannels.Count, true)
                .AddField(Locate("ChannelCount"), $"{server.TextChannels.Count + server.VoiceChannels.Count} ({Constants.TextEmote} {server.TextChannels.Count} **|** {Constants.VoiceEmote} {server.VoiceChannels.Count})", true)
                .AddField(Locate("RoleCount"), server.Roles.Count, true)

                .AddField(Locate("DefaultChannel"), server.DefaultChannel?.Mention ?? Locate("None"), true)
                .AddField(Locate("Region"), Format.Code(server.VoiceRegionId), true)
                .AddField(Locate("VerificationLevel"), Locate(server.VerificationLevel.ToString()), true)

                .AddField(Locate("BoostTier"), (int)server.PremiumTier, true)
                .AddField(Locate("BoostCount"), server.PremiumSubscriptionCount, true)
                .AddField(Locate("ServerFeatures"), server.Features.Count == 0 ? Locate("None") : string.Join(", ", server.Features), true);

            if (server.HasAllMembers)
            {
                builder.AddField(Locate("Members"), $"{server.MemberCount} (Bots: {server.Users.Count(x => x.IsBot)}) **|** " +
                $"{Constants.OnlineEmote} {server.Users.Count(x => x.Status == UserStatus.Online)} **|** " +
                $"{Constants.IdleEmote} {server.Users.Count(x => x.Status == UserStatus.Idle)} **|** " +
                $"{Constants.DndEmote} {server.Users.Count(x => x.Status == UserStatus.DoNotDisturb)} **|** " +
                $"{Constants.StreamingEmote} {server.Users.Count(x => x.Activities.Any(x => x.Type == ActivityType.Streaming))} **|** " +
                $"{Constants.OfflineEmote} {server.Users.Count(x => x.Status == UserStatus.Offline)}");
            }
            else
            {
                builder.AddField(Locate("Members"), server.MemberCount, true);
            }

            //if (server.Emotes.Count == 0)
            //{
            //    builder.AddField("Emotes", Locate("None"), false);
            //}
            //else
            //{
            //    var chunks = string.Join(" ", server.Emotes.Select(x => x.ToString()))
            //        .SplitToLines(EmbedFieldBuilder.MaxFieldValueLength)
            //        .ToList();
            //    for (int i = 0; i < chunks.Count; i++)
            //    {
            //        builder.AddField(i == 0 ? "Emotes" : "\u200B", chunks[i], false);
            //    }
            //}
            //builder.AddField("Emotes: ", server.Emotes.Any() ? string.Join(" ", server.Emotes.ToList()).Truncate(1021) + "..." : "(None)", false);
            builder.AddField(Locate("CreatedAt"), server.CreatedAt, true);
            builder.WithThumbnailUrl(server.Features.Any(x => x == "ANIMATED_ICON") ? Path.ChangeExtension(server.IconUrl, "gif") : server.IconUrl);
            //if (server.Features.Any(x => x == "BANNER"))
            //{
            //}
            builder.WithColor(FergunConfig.EmbedColor);
            await ReplyAsync(embed: builder.Build());
            return FergunResult.FromSuccess();
        }

        [Command("snipe", RunMode = RunMode.Async)]
        [Summary("snipeSummary")]
        [Example("#help")]
        public async Task Snipe([Summary("snipeParam1")] IMessageChannel channel = null)
        {
            channel ??= Context.Channel;
            var message = FergunClient.MessageCache
                .Where(x => x.SourceEvent == SourceEvent.MessageDeleted && x.Channel.Id == channel.Id)
                .OrderByDescending(x => x.CachedAt)
                .FirstOrDefault();

            var builder = new EmbedBuilder();
            if (message == null)
            {
                builder.WithDescription(string.Format(Locate("NothingToSnipe"), MentionUtils.MentionChannel(channel.Id)));
            }
            else
            {
                string text = !string.IsNullOrEmpty(message.Content) ? message.Content : message.Attachments.Count > 0 ? $"({Locate("Attachment")})" : "?";

                builder.WithAuthor(message.Author)
                    .WithDescription(text.Truncate(EmbedBuilder.MaxDescriptionLength))
                    .WithFooter($"{Locate("In")} #{message.Channel.Name}")
                    .WithTimestamp(message.CreatedAt);
            }
            builder.WithColor(FergunConfig.EmbedColor);

            await ReplyAsync(embed: builder.Build());
        }

        [LongRunning]
        [Command("translate", RunMode = RunMode.Async)]
        [Summary("translateSummary")]
        [Alias("tr")]
        [Example("es hello world")]
        public async Task<RuntimeResult> Translate([Summary("translateParam1")] string target,
            [Remainder, Summary("translateParam2")] string text)
        {
            if (!GoogleTranslator.IsLanguageSupported(new Language("", target)))
            {
                return FergunResult.FromError($"{Locate("InvalidLanguage")}\n{string.Join(" ", Translators.SupportedLanguages.Select(x => Format.Code(x)))}");
            }

            var result = await TranslateSimpleAsync(text, target);
            if (result.Error != null)
            {
                return FergunResult.FromError(Locate(result.Error));
            }

            if (result.Text.Length > EmbedFieldBuilder.MaxFieldValueLength - 10)
            {
                try
                {
                    var response = await Hastebin.UploadAsync(result.Text);
                    result.Text = Format.Url(Locate("HastebinLink"), response.GetLink());
                }
                catch (HttpRequestException e)
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", "Paste: Error while uploading text to Hastebin", e));
                    result.Text = Format.Code(result.Text.Truncate(EmbedFieldBuilder.MaxFieldValueLength - 10), "md");
                }
            }
            else
            {
                result.Text = Format.Code(result.Text.Truncate(EmbedFieldBuilder.MaxFieldValueLength - 10), "md");
            }

            var builder = new EmbedBuilder()
                .WithTitle(Locate("TranslationResults"))
                .AddField(Locate("SourceLanguage"), result.Source?.FullName ?? "???", false)
                .AddField(Locate("TargetLanguage"), result.Target.FullName, false)
                .AddField(Locate("Result"), result.Text, false)
                .WithThumbnailUrl("https://fergun.is-inside.me/u7fSdkx8.png")
                .WithColor(FergunConfig.EmbedColor);

            await ReplyAsync(embed: builder.Build());

            return FergunResult.FromSuccess();
        }

        [RequireBotPermission(ChannelPermission.AttachFiles, ErrorMessage = "BotRequireAttachFiles")]
        [LongRunning]
        [Command("tts", RunMode = RunMode.Async), Ratelimit(2, Constants.GlobalRatelimitPeriod, Measure.Minutes)]
        [Summary("Text to speech.")]
        [Alias("texttospeech", "t2s")]
        [Example("en hello world")]
        public async Task<RuntimeResult> Tts([Summary("ttsParam1")] string target,
            [Remainder, Summary("ttsParam2")] string text)
        {
            target = target.ToLowerInvariant();
            text = text.ToLowerInvariant();

            if (!GoogleTranslator.IsLanguageSupported(new Language("", target)))
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"TTS: Target language not supported ({target})"));
                //return CustomResult.FromError(GetValue("InvalidLanguage"));
                text = $"{target} {text}";
                target = "en";
            }

            Stream stream;
            try
            {
                stream = await GoogleTTS.GetTtsStreamAsync(text, target);
            }
            catch (HttpRequestException e)
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", "TTS: Error while getting TTS", e));
                return FergunResult.FromError(Locate("AnErrorOccurred"));
            }

            await Context.Channel.SendCachedFileAsync(Cache, Context.Message.Id, stream, "tts.mp3");

            return FergunResult.FromSuccess();
        }

        // The attribute order matters
        [RequireNsfw(ErrorMessage = "NSFWOnly")]
        [LongRunning]
        [Command("urban", RunMode = RunMode.Async)]
        [Summary("urbanSummary")]
        [Alias("ud")]
        [Example("pog")]
        public async Task<RuntimeResult> Urban([Remainder, Summary("urbanParam1")] string query = null)
        {
            UrbanResponse search = null;
            CachedPages cached = null;

            query = query?.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Urban: Getting random words..."));
                try
                {
                    search = UrbanApi.GetRandomWords();
                }
                catch (WebException e)
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Urban: Error in API", e));
                    return FergunResult.FromError($"Error in Urban Dictionary API: {e.Message}");
                }
            }
            else
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Urban: Query \"{query}\""));
                cached = UrbanCache.FirstOrDefault(x => x.Query == query);
                if (cached == null)
                {
                    try
                    {
                        search = UrbanApi.SearchWord(query);
                    }
                    catch (WebException e)
                    {
                        await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Urban: Error in API", e));
                        return FergunResult.FromError($"Error in Urban Dictionary API: {e.Message}");
                    }
                    if (search.Definitions.Count == 0)
                    {
                        return FergunResult.FromError(Locate("NoResults"));
                    }
                }
            }

            var pages = new List<PaginatedMessage.Page>();
            if (cached == null)
            {
                foreach (var item in search.Definitions)
                {
                    // Nice way to replace all ocurrences to a custom string.
                    item.Definition = _bracketReplacer.Replace(item.Definition,
                                                              m => Format.Url(m.Groups[1].Value, $"https://urbandictionary.com/define.php?term={Uri.EscapeDataString(m.Groups[1].Value)}"));
                    if (!string.IsNullOrEmpty(item.Example))
                    {
                        item.Example = _bracketReplacer.Replace(item.Example,
                                                              m => Format.Url(m.Groups[1].Value, $"https://urbandictionary.com/define.php?term={Uri.EscapeDataString(m.Groups[1].Value)}"));
                    }
                    List<EmbedFieldBuilder> fields = new List<EmbedFieldBuilder>
                    {
                        new EmbedFieldBuilder()
                        {
                            Name = Locate("Example"),
                            Value = string.IsNullOrEmpty(item.Example) ? Locate("NoExample") : item.Example.Truncate(EmbedFieldBuilder.MaxFieldValueLength),
                            IsInline = false
                        },
                        new EmbedFieldBuilder()
                        {
                            Name = "👍",
                            Value = item.ThumbsUp,
                            IsInline = true
                        },
                        new EmbedFieldBuilder()
                        {
                            Name = "👎",
                            Value = item.ThumbsDown,
                            IsInline = true
                        }
                    };

                    var author = new EmbedAuthorBuilder()
                        .WithName($"{Locate("By")} {item.Author}");

                    pages.Add(new PaginatedMessage.Page()
                    {
                        Author = author,
                        Title = item.Word.Truncate(EmbedBuilder.MaxTitleLength),
                        Description = item.Definition.Truncate(EmbedBuilder.MaxDescriptionLength),
                        Url = item.Permalink,
                        Fields = fields,
                        TimeStamp = item.WrittenOn
                    });
                }
                UrbanCache.Add(new CachedPages(query, pages, true));
            }
            else
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", "Urban: Found a cached result."));
                pages = cached.Pages;
            }

            var pager = new PaginatedMessage()
            {
                //Title = result.Item2.First(),
                Pages = pages,
                Color = new Discord.Color(FergunConfig.EmbedColor),
                Options = new PaginatedAppearanceOptions()
                {
                    FooterFormat = $"Urban Dictionary {(string.IsNullOrWhiteSpace(query) ? "(Random words)" : "")} - {Locate("PaginatorFooter")}",
                    Timeout = TimeSpan.FromMinutes(10),
                    ActionOnTimeout = ActionOnTimeout.DeleteReactions
                }
            };

            var reactions = new ReactionList()
            {
                First = pages.Count >= 3,
                Backward = true,
                Forward = true,
                Last = pages.Count >= 3,
                Stop = true,
                Jump = pages.Count >= 4,
                Info = false
            };

            await PagedReplyAsync(pager, reactions);

            return FergunResult.FromSuccess();
        }

        [Command("userinfo")]
        [Summary("userinfoSummary")]
        [Alias("ui", "user", "whois")]
        [Example("Fergun#6839")]
        public async Task<RuntimeResult> Userinfo([Remainder, Summary("userinfoParam1")] IUser user = null)
        {
            user ??= Context.User;
            // Prevent getting error 404 while downloading the avatar getting the user from REST.
            var restUser = await Context.Client.Rest.GetUserAsync(user.Id);

            string avatarUrl = restUser.GetAvatarUrl(Discord.ImageFormat.Auto, 2048) ?? restUser.GetDefaultAvatarUrl();
            string thumbnail = restUser.GetAvatarUrl(Discord.ImageFormat.Png, 128) ?? restUser.GetDefaultAvatarUrl();

            System.Drawing.Color avatarColor;
            using (Stream response = await _httpClient.GetStreamAsync(new Uri(thumbnail)))
            using (Bitmap img = new Bitmap(response))
            {
                avatarColor = img.GetAverageColor();
            }

            string activities = "";
            foreach (var activity in user.Activities)
            {
                if (activity != null)
                {
                    if (activity.Type == ActivityType.CustomStatus)
                    {
                        activities += Format.Bold((activity as CustomStatusGame).State);
                    }
                    else
                    {
                        activities += $"{activity.Type} {Format.Bold(activity.Name)}";
                    }
                    activities += "\n";
                }
            }
            if (string.IsNullOrWhiteSpace(activities))
            {
                activities = Locate("None");
            }
            var guildUser = user as IGuildUser;

            string clients = "?";
            if (user.ActiveClients.Count > 0)
            {
                clients = string.Join(" ", user.ActiveClients.Select(x =>
                x == ClientType.Desktop ? "🖥" :
                x == ClientType.Mobile ? "📱" :
                x == ClientType.Web ? "🌐" : ""));
            }
            var builder = new EmbedBuilder()
                .WithTitle(Locate("UserInfo"))
                .AddField(Locate("Name"), user.ToString(), false)
                .AddField("Nickname", guildUser?.Nickname ?? Locate("None"), false)
                .AddField("ID", user.Id, false)
                .AddField(Locate("Activity"), activities, true)
                .AddField(Locate("ActiveClients"), clients, true)
                .AddField(Locate("IsBot"), Locate(user.IsBot), false)
                .AddField(Locate("CreatedAt"), user.CreatedAt)
                .AddField(Locate("GuildJoinDate"), guildUser?.JoinedAt?.ToString() ?? "N/A")
                .AddField(Locate("BoostingSince"), guildUser?.PremiumSince?.ToString() ?? "N/A")
                //.AddField(GetValue("Roles"), !(user is IGuildUser) || guildUser.RoleIds.Count == 1 ? GetValue("None") : string.Join(", ", guildUser.RoleIds.Skip(1).Select(x => Context.Guild.GetRole(x).Mention)))
                .WithThumbnailUrl(avatarUrl)
                .WithColor(new Discord.Color(avatarColor.R, avatarColor.G, avatarColor.B));

            await ReplyAsync(embed: builder.Build());

            return FergunResult.FromSuccess();
        }

        [LongRunning]
        [Command("wikipedia", RunMode = RunMode.Async)]
        [Summary("wikipediaSummary")]
        [Alias("wiki")]
        [Example("Discord")]
        public async Task<RuntimeResult> Wikipedia([Remainder, Summary("wikipediaParam1")] string query)
        {
            string response;
            using (WebClient wc = new WebClient())
            {
                // I would want to know who did the awful json response structure.
                response = await wc.DownloadStringTaskAsync($"https://{GetLanguage()}.wikipedia.org/w/api.php?action=opensearch&search={Uri.EscapeDataString(query)}&format=json");
            }
            //response = '[' + SearchResponse.Substring(SearchResponse.IndexOf(',') + 1, SearchResponse.Length - SearchResponse.IndexOf(',') - 1);
            List<dynamic> search = search = JsonConvert.DeserializeObject<List<dynamic>>(response);
            string langToUse = GetLanguage();
            if (search[1].Count == 0)
            {
                if (langToUse == "en")
                {
                    return FergunResult.FromError(Locate("NoResults"));
                }
                else
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", "Wikipedia: No results found on non-English Wikipedia. Searching on English Wikipedia."));
                    langToUse = "en";
                    using (WebClient wc = new WebClient())
                    {
                        response = await wc.DownloadStringTaskAsync($"https://{langToUse}.wikipedia.org/w/api.php?action=opensearch&search={Uri.EscapeDataString(query)}&format=json");
                    }
                    search = JsonConvert.DeserializeObject<List<dynamic>>(response);
                    if (search[1].Count == 0)
                    {
                        return FergunResult.FromError(Locate("NoResults"));
                    }
                }
            }
            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Wikipedia: Results count: {search[1].Count}"));

            using (WebClient wc = new WebClient())
            {
                string articleUrl = search[search.Count - 1][0];
                string apiUrl = $"https://{langToUse}.wikipedia.org/api/rest_v1/page/summary/{articleUrl.Substring(30)}";
                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Wikipedia: Downloading article from url: {apiUrl}"));

                response = await wc.DownloadStringTaskAsync(apiUrl);
            }
            var article = JsonConvert.DeserializeObject<WikiArticle>(response);

            var builder = new EmbedBuilder()
                .WithAuthor(Context.User)
                .WithTitle(article.Title.Truncate(EmbedBuilder.MaxTitleLength))
                .WithDescription(article.Extract.Truncate(EmbedBuilder.MaxDescriptionLength))
                .WithFooter(Locate("WikipediaSearch"))
                .WithThumbnailUrl("https://upload.wikimedia.org/wikipedia/commons/thumb/8/80/Wikipedia-logo-v2.svg/500px-Wikipedia-logo-v2.svg.png")
                .WithColor(FergunConfig.EmbedColor);

            string url = Context.User.ActiveClients.Any(x => x == ClientType.Mobile) ? article.ContentUrls.Mobile.Page : article.ContentUrls.Desktop.Page;
            if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                builder.WithUrl(url);
            }
            else
            {
                url = WebUtility.UrlDecode(url);
                if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
                {
                    builder.WithUrl(url);
                }
                else
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", $"Wikipedia: Invalid url to article: {url}"));
                }
            }

            if (!Context.IsPrivate && (Context.Channel as ITextChannel).IsNsfw && article.Originalimage?.Source != null)
            {
                string decodedUrl = Uri.UnescapeDataString(article.Originalimage.Source);
                if (Uri.IsWellFormedUriString(decodedUrl, UriKind.Absolute))
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Command", $"Wikipedia: Using image Url: {decodedUrl}"));
                    builder.ThumbnailUrl = decodedUrl;
                }
                else
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Command", $"Wikipedia: Invalid image url: {decodedUrl}"));
                }
            }

            await ReplyAsync(embed: builder.Build());
            return FergunResult.FromSuccess();
        }

        [Command("xkcd")]
        [Summary("xkcdSummary")]
        [Example("1000")]
        public async Task<RuntimeResult> Xkcd([Summary("xkcdParam1")] int? number = null)
        {
            UpdateLastComic();
            if (number != null && (number < 1 || number > _lastComic.Num))
            {
                return FergunResult.FromError(string.Format(Locate("InvalidxkcdNumber"), _lastComic.Num));
            }
            if (number == 404)
            {
                return FergunResult.FromError("404 Not Found");
            }
            string response;
            using (WebClient wc = new WebClient())
            {
                response = await wc.DownloadStringTaskAsync($"https://xkcd.com/{number ?? RngInstance.Next(1, _lastComic.Num)}/info.0.json");
            }

            XkcdResponse xkcd = JsonConvert.DeserializeObject<XkcdResponse>(response);

            var builder = new EmbedBuilder()
                .WithTitle(xkcd.Title.Truncate(EmbedBuilder.MaxTitleLength))
                .WithUrl($"https://xkcd.com/{xkcd.Num}/")
                .WithImageUrl(xkcd.Img)
                .WithFooter(xkcd.Alt.Truncate(EmbedFooterBuilder.MaxFooterTextLength))
                .WithTimestamp(new DateTime(int.Parse(xkcd.Year), int.Parse(xkcd.Month), int.Parse(xkcd.Day)));

            await ReplyAsync(embed: builder.Build());
            return FergunResult.FromSuccess();
        }

        [RequireNsfw(ErrorMessage = "NSFWOnly")]
        //[LongRunning]
        [Command("ytrandom", RunMode = RunMode.Async)]
        [Summary("ytrandomSummary")]
        [Alias("ytrand")]
        public async Task<RuntimeResult> Ytrandom()
        {
            if (VideoCache.IsEmpty)
            {
                if (_isCreatingCache)
                {
                    return FergunResult.FromError(Locate("CreatingVideoCache"));
                }
                _isCreatingCache = true;

                await ReplyAsync(Locate("EmptyCache"));
                await Context.Channel.TriggerTypingAsync();
                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Ytrandom", $"Creating video cache for {Context.User} in {Context.Display()}"));

                var tasks = CreateVideoTasks();
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch
                {
                    throw;
                }
                finally
                {
                    _isCreatingCache = false;
                }
            }
            if (!VideoCache.TryTake(out string id))
            {
                return FergunResult.FromError(Locate("AnErrorOccurred"));
            }
            await ReplyAsync($"https://www.youtube.com/watch?v={id}");
            return FergunResult.FromSuccess();
        }

        // Helper methods

        private static List<Task> CreateVideoTasks()
        {
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < FergunConfig.VideoCacheSize; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        string randStr = RandomString(RngInstance.Next(5, 7));
                        //var items = await YTClient.SearchVideosAsync(rand, 1);
                        var items = await _ytClient.Search.GetVideosAsync(randStr).BufferAsync(5);
                        if (items.Count != 0)
                        {
                            string randomId = items[RngInstance.Next(items.Count)].Id;
                            VideoCache.Add(randomId);
                            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Ytrandom", $"Added 1 item to Video cache (random string: {randStr}, search count: {items.Count}, selected id: {randomId}), total count: {VideoCache.Count}"));
                            break;
                        }
                        else
                        {
                            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Ytrandom", $"No videos found on {randStr}"));
                        }
                    }
                }));
            }
            return tasks;
        }

        private static Task<HttpResponseMessage> GetUrlResponseHeadersAsync(string url)
        {
            return _httpClient.GetAsync(new UriBuilder(url).Uri, HttpCompletionOption.ResponseHeadersRead);
        }

        private static async Task<long?> GetUrlContentLengthAsync(string url)
        {
            var response = await GetUrlResponseHeadersAsync(url);
            return response.Content.Headers.ContentLength;
        }

        private static async Task<string> GetUrlMediaTypeAsync(string url)
        {
            var response = await GetUrlResponseHeadersAsync(url);
            return response.Content.Headers.ContentType.MediaType.ToLowerInvariant();
        }

        private static async Task<bool> IsImageUrlAsync(string url)
        {
            //Uri uri;
            //try
            //{
            //    uri = new UriBuilder(url).Uri;
            //}
            //catch
            //{
            //    return false;
            //}
            string mediaType;
            try
            {
                mediaType = await GetUrlMediaTypeAsync(url);
            }
            catch (UriFormatException)
            {
                return false;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            return mediaType.ToLowerInvariant().StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<(string, string)> OcrSimpleAsync(string url)
        {
            if (!Enum.TryParse((await GetUrlMediaTypeAsync(url)).Substring(6), true, out OCRSpace.FileType fileType))
            {
                return ("InvalidFileType", null);
            }

            OCRSpace.OCRSpaceResponse ocr;
            try
            {
                ocr = await OCRSpace.PerformOcrFromUrlAsync(FergunConfig.OCRSpaceApiKey, url, fileType: fileType, ocrEngine: OCRSpace.OCREngine.Engine1);
            }
            catch (WebException e)
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "OcrSimple", "Error in OCR", e));
                return ("OcrApiError", null);
            }

            if (ocr.IsErroredOnProcessing || ocr.OcrExitCode != 1)
            {
                return (ocr.ErrorMessage[0], null);
            }
            else if (string.IsNullOrWhiteSpace(ocr.ParsedResults[0].ParsedText))
            {
                return ("OcrEmpty", null);
            }

            string text = ocr.ParsedResults[0].ParsedText
                .Replace("`", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();

            return (ocr.ProcessingTimeInMilliseconds, text);
        }

        private static async Task<SimpleTranslationResult> TranslateSimpleAsync(string text, Language target, Language source)
            => await TranslateSimpleAsync(text, target.ISO639, source == Language.Auto ? "" : source.ISO639);

        private static async Task<SimpleTranslationResult> TranslateSimpleAsync(string text, string target, string source = "")
        {
            string resultError = null;
            string resultTranslation = null;
            Language resultTarget = GoogleTranslator.GetLanguageByISO(target);
            Language resultSource = null;

            bool useBing = false;
            text = text.Replace("`", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

            await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Translator", $"Target: {resultTarget}"));
            try
            {
                var translator = new GoogleTranslator();
                var result = await translator.TranslateLiteAsync(text, string.IsNullOrEmpty(source) ? Language.Auto : new Language("", source), resultTarget);

                resultTranslation = result.MergedTranslation;
                resultSource = result.LanguageDetections[0].Language;

                await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Translator", $"Detected language: {resultSource}"));
            }
            catch (GoogleTranslateIPBannedException e)
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Translator", "Error while translating, using Bing", e));
                useBing = true;
            }
            catch (HttpRequestException e)
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Translator", "Error while translating, using Bing", e));
                useBing = true;
            }
            catch (NullReferenceException e)
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Translator", "Error while translating, using Bing", e));
                useBing = true;
            }
            catch (ArgumentNullException e)
            {
                await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Translator", "Error while translating, using Bing", e));
                useBing = true;
            }
            if (useBing)
            {
                try
                {
                    var result = await Translators.TranslateBingAsync(text, target);

                    resultTranslation = result[0].Translations[0].Text;
                    resultSource = GoogleTranslator.GetLanguageByISO(result[0].DetectedLanguage.Language);

                    await _logService.LogAsync(new LogMessage(LogSeverity.Verbose, "Translator", $"Detected language: {result[0].DetectedLanguage.Language}"));
                }
                catch (JsonSerializationException e)
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Translator", "Error while translating", e));
                    resultError = "ErrorInTranslation";
                }
                catch (HttpRequestException e)
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Translator", "Error while translating", e));
                    resultError = "ErrorInTranslation";
                }
                catch (ArgumentException e)
                {
                    await _logService.LogAsync(new LogMessage(LogSeverity.Warning, "Translator", "Error while translating", e));
                    resultError = "LanguageNotFound";
                }
            }

            return new SimpleTranslationResult
            {
                Error = resultError,
                Text = resultTranslation,
                Source = resultSource,
                Target = resultTarget
            };
        }

        private static void UpdateLastComic()
        {
            if (_timeToCheckComic != null && DateTimeOffset.UtcNow < _timeToCheckComic)
            {
                return;
            }
            string response;
            using (WebClient wc = new WebClient())
            {
                response = wc.DownloadString("https://xkcd.com/info.0.json");
            }
            _lastComic = JsonConvert.DeserializeObject<XkcdResponse>(response);
            _timeToCheckComic = DateTimeOffset.UtcNow.AddDays(1);
        }

        /// <summary>
        /// Gets the url from the last x messages / embeds /attachments.
        /// </summary>
        /// <param name="messageCount">The number of messages to search.</param>
        /// <param name="onlyImage">Get only urls of images.</param>
        /// <param name="maxSize">The maximum file size in bytes, <see cref="Constants.AttachmentSizeLimit"/> by default.</param>
        /// <returns>The url on success, or null and the error reason.</returns>
        private async Task<(string, string)> GetLastUrlAsync(int messageCount, bool onlyImage, string url = null, long maxSize = Constants.AttachmentSizeLimit)
        {
            long? size = null;
            //If the message that executed the command contains any suitable attachment or url
            if (url != null || Context.Message.Attachments.Count > 0)
            {
                if (Context.Message.Attachments.Count > 0)
                {
                    var attachment = Context.Message.Attachments.First();
                    if (onlyImage && attachment.Width == null && attachment.Height == null)
                    {
                        return (null, Locate("AttachmentNotImage"));
                    }
                    url = attachment.Url;
                }
                if (onlyImage && !await IsImageUrlAsync(url))
                {
                    return (null, Locate("UrlNotImage"));
                }
                size = await GetUrlContentLengthAsync(url);
                if (size != null && size > maxSize)
                {
                    return (null, Locate("ImageTooLarge"));
                }
                return (url, null);
            }

            //Get the last x messages of the current channel
            var messages = await Context.Channel.GetMessagesAsync(messageCount, messageCount > 0 ? CacheMode.CacheOnly : CacheMode.AllowDownload).FlattenAsync();

            //Try to get the last message with any attachment, embed image url or that contains an url
            var filtered = messages.FirstOrDefault(x =>
            x.Attachments.Any(y => !onlyImage || y.Width != null && y.Height != null)
            || x.Embeds.Any(y => !onlyImage || y.Image != null || y.Thumbnail != null)
            || _linkParser.IsMatch(x.Content));

            //If there's no results, return nothing
            if (filtered == null)
            {
                return (null, string.Format(Locate("ImageNotFound"), messageCount));
            }

            //Note: attachments and embeds can contain text but i'm prioritizing the previous ones
            // Priority order: attachments > embeds > text
            if (filtered.Attachments.Count > 0)
            {
                //var attachment = filtered.Attachments.First();
                //if (OnlyImage && attachment.Width != null && attachment.Height != null)//!IsImageUrl(filtered.Attachments.First().Url))
                //{
                //    return (null, string.Format(Locate("ImageNotFound"), messageCount));
                //}
                url = filtered.Attachments.First().Url;
                size = filtered.Attachments.First().Size;
            }
            else if (filtered.Embeds.Count > 0)
            {
                var embed = filtered.Embeds.First();
                var image = embed.Image;
                var thumbnail = embed.Thumbnail;
                if (onlyImage)
                {
                    if (image?.Height != null && image?.Width != null)
                    {
                        url = image?.Url;
                    }
                    else if (thumbnail?.Height != null && thumbnail?.Width != null)
                    {
                        url = thumbnail?.Url;
                    }
                    else
                    {
                        return (null, string.Format(Locate("ImageNotFound"), messageCount));
                    }

                    // the image can still be invalid
                    if (!await IsImageUrlAsync(url))
                    {
                        return (null, string.Format(Locate("ImageNotFound"), messageCount));
                    }
                }
                else
                {
                    url = embed.Url ?? image?.Url ?? thumbnail?.Url;
                }
            }
            else
            {
                string match = _linkParser.Match(filtered.Content).Value;
                if (onlyImage && !await IsImageUrlAsync(match))
                {
                    return (null, string.Format(Locate("ImageNotFound"), messageCount));
                }
                url = match;
            }
            if (filtered.Attachments.Count == 0)
            {
                size = await GetUrlContentLengthAsync(url);
            }

            if (size != null && size > maxSize)
            {
                return (null, Locate("ImageTooLarge"));
            }
            return (url, null);
        }

        public static string RandomString(int length)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, length)
              .Select(s => s[RngInstance.Next(s.Length)]).ToArray());
        }
    }

    public class CachedPages
    {
        public CachedPages(string query, List<PaginatedMessage.Page> pages, bool isNsfw)
        {
            Query = query;
            Pages = pages;
            IsNsfw = isNsfw;
        }

        public bool IsNsfw { get; set; }
        public List<PaginatedMessage.Page> Pages { get; set; }
        public string Query { get; set; }
    }

    public class SimpleTranslationResult
    {
        public string Error { get; set; }
        public Language Source { get; set; }
        public Language Target { get; set; }
        public string Text { get; set; }
    }
}