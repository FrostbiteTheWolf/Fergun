using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;

namespace Fergun.Interactive
{
    /// <summary>
    /// The paginated message callback.
    /// </summary>
    internal class PaginatedMessageCallback : IInteractionCallback
    {
        /// <summary>
        /// The timeout.
        /// </summary>
        public TimeSpan? Timeout => _pager.Options.Timeout;

        /// <summary>
        /// The page count.
        /// </summary>
        private readonly int _pages;

        /// <summary>
        /// The current page.
        /// </summary>
        private int _page = 1;

        /// <summary>
        /// The paginated message.
        /// </summary>
        private readonly PaginatedMessage _pager;

        private readonly string _notCommandUserText;

        /// <summary>
        /// Initializes a new instance of the <see cref="PaginatedMessageCallback"/> class.
        /// </summary>
        /// <param name="interactive">
        /// The interactive service.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        /// <param name="pager">
        /// The pager.
        /// </param>
        /// <param name="criterion">
        /// The criterion.
        /// </param>
        public PaginatedMessageCallback(InteractiveService interactive,
            SocketCommandContext context,
            PaginatedMessage pager,
            ICriterion<SocketInteraction> criterion = null, string notCommandUserText = null)
        {
            Interactive = interactive;
            Context = context;
            Criterion = criterion ?? new EmptyCriterion<SocketInteraction>();
            _pager = pager;
            _pages = _pager.Pages?.Count() ?? default;
            _notCommandUserText = notCommandUserText;
        }

        /// <summary>
        /// Gets the command context.
        /// </summary>
        public SocketCommandContext Context { get; }

        /// <summary>
        /// Gets the interactive service.
        /// </summary>
        public InteractiveService Interactive { get; }

        /// <summary>
        /// Gets the criterion.
        /// </summary>
        public ICriterion<SocketInteraction> Criterion { get; }

        /// <summary>
        /// Gets the message.
        /// </summary>
        public IUserMessage Message { get; private set; }

        public ReactionList Buttons { get; private set; }

        /// <summary>
        /// Sends the paginated message with the provided reaction list.
        /// </summary>
        /// <param name="reactionList">
        /// The reactions to add.
        /// </param>
        /// <param name="oldMessage">
        /// An old message to reuse.
        /// </param>
        /// <returns>
        /// A task representing the asynchronous operation.
        /// </returns>
        internal async Task DisplayAsync(ReactionList reactionList, IUserMessage oldMessage)
        {
            var embed = BuildEmbed();
            Buttons = reactionList;

            var component = _pages <= 1 ? null : BuildComponent();

            if (oldMessage == null)
            {
                Message = await Context.Channel.SendMessageAsync(_pager.Text, embed: embed, component: component).ConfigureAwait(false);
            }
            else
            {
                // Remove the old message callback
                Interactive.RemoveInteractionCallback(oldMessage);

                await oldMessage.ModifyAsync(x =>
                {
                    x.Content = _pager.Text;
                    x.Embed = embed;
                    x.Components = component;
                }).ConfigureAwait(false);

                Message = oldMessage;
            }

            if (_pages <= 1) return;

            Interactive.AddInteractionCallback(Message, this);

            _ = Task.Run(async () =>
            {
                if (Timeout.HasValue)
                {
                    await Task.Delay(Timeout.Value).ConfigureAwait(false);
                    await OnStopAsync(Message).ConfigureAwait(false);
                }
            });
        }

        private async Task OnStopAsync(IUserMessage message, SocketInteraction interaction = null)
        {
            if (!Interactive.ContainsInteraction(Message.Id)) return;
            Interactive.RemoveInteractionCallback(Message);

            if (interaction != null)
            {
                await interaction.RespondAsync(embed: BuildEmbed(), type: InteractionResponseType.UpdateMessage, component: BuildComponent(false, false, false, false, false))
                    .ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await message.ModifyAsync(x => x.Components = BuildComponent(false, false, false, false, false)).ConfigureAwait(false);
                }
                catch (HttpException) { }
            }
        }

        /// <summary>
        /// Handles the interaction callback.
        /// </summary>
        /// <returns>
        /// A task representing the asynchronous operation.
        /// </returns>
        public async Task<bool> HandleCallbackAsync(SocketInteraction interaction, string button, bool isCommandUser)
        {
            if (!isCommandUser)
            {
                await interaction.RespondAsync(_notCommandUserText ?? "You can't use this interaction.", type: InteractionResponseType.ChannelMessageWithSource, ephemeral: true);
                return false;
            }

            if (button == _pager.Options.First.Name)
            {
                _page = 1;
            }
            else if (button == _pager.Options.Back.Name)
            {
                _page--;
            }
            else if (button == _pager.Options.Next.Name)
            {
                _page++;
            }
            else if (button == _pager.Options.Last.Name)
            {
                _page = _pages;
            }
            else if (button == _pager.Options.Stop.Name)
            {
                await OnStopAsync(Message, interaction).ConfigureAwait(false);
                return true;
            }
            else if (button == _pager.Options.Jump.Name)
            {
                _ = Task.Run(async () =>
                {
                    var criteria = new Criteria<SocketMessage>()
                        .AddCriterion(new EnsureSourceChannelCriterion())
                        .AddCriterion(new EnsureFromUserCriterion(Context.User.Id))
                        .AddCriterion(new EnsureIsIntegerCriterion());

                    var response = await Interactive.NextMessageAsync(Context, criteria, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

                    if (response == null || !int.TryParse(response.Content, out int requestedPage))
                    {
                        return;
                    }

                    _ = response.DeleteAsync().ConfigureAwait(false);

                    if (requestedPage < 1 || requestedPage == _page || requestedPage > _pages)
                    {
                        return;
                    }

                    _page = requestedPage;

                    await RenderAsync().ConfigureAwait(false);
                });
                return false;
            }
            else
            {
                return false;
            }

            await RenderAsync(interaction).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Builds the embed with the current page.
        /// </summary>
        /// <returns>
        /// The <see cref="Embed"/>.
        /// </returns>
        private Embed BuildEmbed()
        {
            if (_pages == 0) return _pager.Build();

            var current = _pager.Pages.ElementAt(_page - 1);
            current.Title ??= _pager.Title;
            current.Description ??= _pager.Description;
            current.Url ??= _pager.Url;
            current.ThumbnailUrl ??= _pager.ThumbnailUrl;
            current.ImageUrl ??= _pager.ImageUrl;
            current.Fields = current.Fields?.Count == 0 ? _pager.Fields : current.Fields;
            current.Timestamp ??= _pager.Timestamp;
            current.Color ??= _pager.Color;
            current.Author ??= _pager.Author;
            current.Footer ??= _pager.Footer ?? new EmbedFooterBuilder
            {
                Text = string.Format(_pager.Options.FooterFormat, _page, _pages)
            };

            return current.Build();
        }

        /// <summary>
        /// Renders an embed page.
        /// </summary>
        /// <returns>
        /// A task representing the asynchronous operation.
        /// </returns>
        private Task RenderAsync() => Message.ModifyAsync(m => m.Embed = BuildEmbed());

        private MessageComponent BuildComponent() =>
            BuildComponent(
                Buttons.First && _page != 1,
                Buttons.Backward && _page != 1,
                _page != _pages && Buttons.Forward,
                _page != _pages && Buttons.Last,
                Buttons.Stop);

        private MessageComponent BuildComponent(bool first, bool back, bool next, bool last, bool stop)
        {
            return new ComponentBuilder()
                .WithButton(null, _pager.Options.First.Name, ButtonStyle.Primary, new Emoji(_pager.Options.First.Name), null, !first)
                .WithButton(null, _pager.Options.Back.Name, ButtonStyle.Primary, new Emoji(_pager.Options.Back.Name), null, !back)
                .WithButton(null, _pager.Options.Next.Name, ButtonStyle.Primary, new Emoji(_pager.Options.Next.Name), null, !next)
                .WithButton(null, _pager.Options.Last.Name, ButtonStyle.Primary, new Emoji(_pager.Options.Last.Name), null, !last)
                .WithButton(null, _pager.Options.Stop.Name, ButtonStyle.Danger, new Emoji(_pager.Options.Stop.Name), null, !stop)
                .Build();
        }

        private async Task<RestUserMessage> RenderAsync(SocketInteraction interaction)
        {
            return await interaction.RespondAsync(embed: BuildEmbed(), type: InteractionResponseType.UpdateMessage, component: BuildComponent()).ConfigureAwait(false);
        }
    }
}