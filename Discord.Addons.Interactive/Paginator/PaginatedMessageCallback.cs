﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord.Commands;
using Discord.WebSocket;

namespace Discord.Addons.Interactive
{
    public class PaginatedMessageCallback : IReactionCallback
    {
        public SocketCommandContext Context { get; }
        public InteractiveService Interactive { get; private set; }
        public IUserMessage Message { get; private set; }

        public RunMode RunMode => RunMode.Sync;
        public ICriterion<SocketReaction> Criterion => _criterion;
        public TimeSpan? Timeout => options.Timeout;

        private readonly ICriterion<SocketReaction> _criterion;
        private readonly PaginatedMessage _pager;

        private PaginatedAppearanceOptions options => _pager.Options;
        private Timer _inactivityTimer;
        private readonly int pages;
        private int page = 1;
        

        public PaginatedMessageCallback(InteractiveService interactive, SocketCommandContext sourceContext, PaginatedMessage pager, ICriterion<SocketReaction> criterion = null)
        {
            Interactive = interactive;
            Context = sourceContext;
            _criterion = criterion ?? new EmptyCriterion<SocketReaction>();
            _pager = pager;
            pages = _pager.Pages.Count();
            if (_pager.Pages is IEnumerable<EmbedFieldBuilder>)
                pages = ((_pager.Pages.Count() - 1) / options.FieldsPerPage) + 1;
        }

        public async Task DisplayAsync()
        {
            var embed = BuildEmbed();
            var message = await Context.Channel.SendMessageAsync(_pager.Content, embed: embed).ConfigureAwait(false);
            Message = message;
            Interactive.AddReactionCallback(message, this);
            
            // Reactions take a while to add, don't wait for them
            _ = Task.Run(async () =>
            {
                List<IEmote> emotes = new List<IEmote>();

                if (options.First != null)
                    emotes.Add(options.First);
                if (options.Back != null)
                    emotes.Add(options.Back);
                if (options.Next != null) 
                    emotes.Add(options.Next);
                if (options.Last != null) 
                    emotes.Add(options.Last);

                var manageMessages = (Context.Channel is IGuildChannel guildChannel) && (Context.User as IGuildUser)!.GetPermissions(guildChannel).ManageMessages;

                if (options.JumpDisplayOptions == JumpDisplayOptions.Always
                    || options.JumpDisplayOptions == JumpDisplayOptions.WithManageMessages && manageMessages)
                    emotes.Add(options.Jump);

                emotes.Add(options.Stop);

                if (options.DisplayInformationIcon)
                    emotes.Add(options.Info);
                
                await message.AddReactionsAsync(emotes.ToArray());
            });
            // TODO: (Next major version) timeouts need to be handled at the service-level!
            if (Timeout.HasValue)
            {
                _inactivityTimer = new Timer(_ =>
                {
                    Interactive.RemoveReactionCallback(message);
                    _ = Message.RemoveAllReactionsAsync();
                }, null, TimeSpan.Zero, Timeout.Value);
            }
        }

        public async Task<bool> HandleCallbackAsync(SocketReaction reaction)
        {
            var emote = reaction.Emote;

            if (emote.Equals(options.First))
                page = 1;
            else if (emote.Equals(options.Next))
            {
                if (page >= pages)
                    return false;
                ++page;
            }
            else if (emote.Equals(options.Back))
            {
                if (page <= 1)
                    return false;
                --page;
            }
            else if (emote.Equals(options.Last))
                page = pages;
            else if (emote.Equals(options.Stop))
            {
                await Message.DeleteAsync().ConfigureAwait(false);
                return true;
            }
            else if (emote.Equals(options.Jump))
            {
                _ = Task.Run(async () =>
                {
                    var criteria = new Criteria<SocketMessage>()
                        .AddCriterion(new EnsureSourceChannelCriterion())
                        .AddCriterion(new EnsureFromUserCriterion(reaction.UserId))
                        .AddCriterion(new EnsureIsIntegerCriterion());
                    var response = await Interactive.NextMessageAsync(Context, criteria, TimeSpan.FromSeconds(15));
                    var request = int.Parse(response.Content);
                    if (request < 1 || request > pages)
                    {
                        _ = response.DeleteAsync().ConfigureAwait(false);
                        await Interactive.ReplyAndDeleteAsync(Context, options.Stop.Name);
                        return;
                    }
                    page = request;
                    _ = response.DeleteAsync().ConfigureAwait(false);
                    await RenderAsync().ConfigureAwait(false);
                });
            }
            else if (emote.Equals(options.Info))
            {
                await Interactive.ReplyAndDeleteAsync(Context, options.InformationText, timeout: options.InfoTimeout);
                return false;
            }
            _ = Message.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
            await RenderAsync().ConfigureAwait(false);
            return false;
        }
        
        protected virtual Embed BuildEmbed()
        {
            if (_pager.Pages is IEnumerable<EmbedBuilder> eb)
                return eb.Skip(page - 1)
                    .First()
                    .WithFooter(f => f.Text = string.Format(options.FooterFormat, page, pages))
                    .Build();

            var builder = new EmbedBuilder()
                .WithAuthor(_pager.Author)
                .WithColor(_pager.Color)
                .WithFooter(f => f.Text = string.Format(options.FooterFormat, page, pages))
                .WithTitle(_pager.Title);
            if (_pager.Pages is IEnumerable<EmbedFieldBuilder> efb)
            {
                builder.Fields = efb.Skip((page - 1) * options.FieldsPerPage).Take(options.FieldsPerPage).ToList();
                builder.Description = _pager.AlternateDescription;
            } 
            else
            {
                builder.Description = _pager.Pages.ElementAt(page - 1).ToString();
            }
            
            return builder.Build();
        }
        private async Task RenderAsync()
        {
            if (Timeout.HasValue)
                _inactivityTimer.Change(TimeSpan.Zero, Timeout!.Value);

            var embed = BuildEmbed();
            await Message.ModifyAsync(m => m.Embed = embed).ConfigureAwait(false);
        }
    }
}
