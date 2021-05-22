﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord.Commands;
using Discord.WebSocket;

// ReSharper disable once CheckNamespace
namespace Discord.Addons.Interactive
{
    public class PaginatedMessageCallback : IReactionCallback
    {
        public SocketCommandContext Context { get; }
        public InteractiveService Interactive { get; private set; }
        public IUserMessage Message { get; private set; }

        public RunMode RunMode => RunMode.Sync;
        public ICriterion<SocketReaction> Criterion => _criterion;
        public TimeSpan? Timeout => Options.Timeout;

        private readonly ICriterion<SocketReaction> _criterion;
        private readonly PaginatedMessage _pager;

        private PaginatedAppearanceOptions Options => _pager.Options;
        private Timer _inactivityTimer;
        private readonly int _pages;
        private int _page = 1;

        private bool _activeJump = false;
        
        public PaginatedMessageCallback(InteractiveService interactive, SocketCommandContext sourceContext, PaginatedMessage pager, ICriterion<SocketReaction> criterion = null)
        {
            Interactive = interactive;
            Context = sourceContext;
            _criterion = criterion ?? new EmptyCriterion<SocketReaction>();
            _pager = pager;
            _pages = _pager.Pages.Count();
            if (_pager.Pages is IEnumerable<EmbedFieldBuilder>)
                _pages = ((_pager.Pages.Count() - 1) / Options.FieldsPerPage) + 1;
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
                if (Options.First != null)
                    await message.AddReactionAsync(Options.First);
                if (Options.Back != null)
                    await message.AddReactionAsync(Options.Back);
                if (Options.Next != null) 
                    await message.AddReactionAsync(Options.Next);
                if (Options.Last != null) 
                    await message.AddReactionAsync(Options.Last);

                var manageMessages = Context.Channel is IGuildChannel guildChannel && (Context.User as IGuildUser)!.GetPermissions(guildChannel).ManageMessages;

                if (Options.JumpDisplayOptions == JumpDisplayOptions.Always
                    || Options.JumpDisplayOptions == JumpDisplayOptions.WithManageMessages && manageMessages)
                    await message.AddReactionAsync(Options.Jump);

                await message.AddReactionAsync(Options.Stop);

                if (Options.DisplayInformationIcon)
                    await message.AddReactionAsync(Options.Info);
            });
            
            // TODO: (Next major version) timeouts need to be handled at the service-level!
            if (Timeout.HasValue)
            {
                _inactivityTimer = new Timer(s =>
                {
                    Interactive.RemoveReactionCallback(message);
                    _ = Message.RemoveAllReactionsAsync();
                    _inactivityTimer.Dispose();
                }, null, Timeout.Value, TimeSpan.Zero);
            }
        }

        public async Task<bool> HandleCallbackAsync(SocketReaction reaction)
        {
            var emote = reaction.Emote;

            if (emote.Equals(Options.First))
                _page = 1;
            else if (emote.Equals(Options.Next))
            {
                if (_page >= _pages)
                    return false;
                ++_page;
            }
            else if (emote.Equals(Options.Back))
            {
                if (_page <= 1)
                    return false;
                --_page;
            }
            else if (emote.Equals(Options.Last))
                _page = _pages;
            else if (emote.Equals(Options.Stop))
            {
                await Message.RemoveAllReactionsAsync().ConfigureAwait(false);
                await _inactivityTimer.DisposeAsync();
                return true;
            }
            else if (emote.Equals(Options.Jump))
            {
                if (!_activeJump)
                {
                    Console.WriteLine($"Jump was activated: {Message.Id}");
                    
                    _ = Task.Run(async () =>
                    {
                        _activeJump = true;
                        
                        var criteria = new Criteria<SocketMessage>()
                            .AddCriterion(new EnsureSourceChannelCriterion())
                            .AddCriterion(new EnsureFromUserCriterion(reaction.UserId))
                            .AddCriterion(new EnsureIsIntegerCriterion());
                    
                        // Display, that a user should enter a page number
                        await Message.ModifyAsync(m => m.Content = " **Enter a page number**\n _ _");
                        // Wait for response
                        var response = await Interactive.NextMessageAsync(Context, criteria, TimeSpan.FromSeconds(15));

                        // Response will never be null my fucking ass
                        if (response != null && int.TryParse(response.Content, out int result) && result > 1 && result < _pages)
                            _page = result;

                        Console.WriteLine("Does it even reach this part?");
                        
                        _ = Message.ModifyAsync(m => m.Content = "");
                        _activeJump = false;
                        _ = response.DeleteAsync().ConfigureAwait(false);
                        await RenderAsync().ConfigureAwait(false);
                    });
                }
                
                // We still want to remove the reaction though
                _ = Message.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
                // We don't want it to render when jump was pressed
                return false;
            }
            else if (emote.Equals(Options.Info))
            {
                await Interactive.ReplyAndDeleteAsync(Context, Options.InformationText, timeout: Options.InfoTimeout);
                return false;
            }
            _ = Message.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
            await RenderAsync().ConfigureAwait(false);
            return false;
        }
        
        protected virtual Embed BuildEmbed()
        {
            if (_pager.Pages is IEnumerable<EmbedBuilder> eb)
                return eb.Skip(_page - 1)
                    .First()
                    .WithFooter(f => f.Text = string.Format(Options.FooterFormat, _page, _pages))
                    .Build();

            var builder = new EmbedBuilder()
                .WithAuthor(_pager.Author)
                .WithColor(_pager.Color)
                .WithFooter(f => f.Text = string.Format(Options.FooterFormat, _page, _pages))
                .WithTitle(_pager.Title);
            if (_pager.Pages is IEnumerable<EmbedFieldBuilder> efb)
            {
                builder.Fields = efb.Skip((_page - 1) * Options.FieldsPerPage).Take(Options.FieldsPerPage).ToList();
                builder.Description = _pager.AlternateDescription;
            } 
            else
            {
                builder.Description = _pager.Pages.ElementAt(_page - 1).ToString();
            }
            
            return builder.Build();
        }
        private async Task RenderAsync()
        {
            if (Timeout.HasValue)
                _inactivityTimer.Change(Timeout!.Value, TimeSpan.Zero);

            var embed = BuildEmbed();
            await Message.ModifyAsync(m => m.Embed = embed).ConfigureAwait(false);
        }
    }
}
