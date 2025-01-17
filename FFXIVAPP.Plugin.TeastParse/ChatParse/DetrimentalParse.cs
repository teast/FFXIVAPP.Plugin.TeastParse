using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FFXIVAPP.Plugin.TeastParse.Actors;
using FFXIVAPP.Plugin.TeastParse.Factories;
using FFXIVAPP.Plugin.TeastParse.Models;
using FFXIVAPP.Plugin.TeastParse.RegularExpressions;
using FFXIVAPP.Plugin.TeastParse.Repositories;
using NLog;
using Sharlayan.Core;

namespace FFXIVAPP.Plugin.TeastParse.ChatParse
{
    internal class DetrimentalParse : BaseChatParse
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly IActorModelCollection _actors;
        private readonly ITimelineCollection _timeline;
        private readonly IDetrimentalFactory _detrimentalFactory;

        private readonly IParseTimer _checkDetrimentals;
        private DateTime? _checkDetrimentalsNext;

        private readonly List<KeyValuePair<ActorModel, DetrimentalModel>> _activeDetrimentals;

        /// <summary>
        /// Contains latest found actions (<see cref="ActionParse" /> for actual parsing of actions)
        /// </summary>
        private readonly IActionCollection _actions;
        private readonly IParseClock _clock;

        protected override Dictionary<ChatcodeType, ChatcodeTypeHandler> Handlers { get; }

        protected override List<ChatCodes> Codes { get; }

        public DetrimentalParse(List<ChatCodes> codes, IActorModelCollection actors, ITimelineCollection timeline, IDetrimentalFactory detrimentalFactory, IActionCollection actions, IParseClock clock, IRepository repository) : base(repository)
        {
            _actors = actors;
            _timeline = timeline;
            _actions = actions;
            _clock = clock;
            _detrimentalFactory = detrimentalFactory;
            _activeDetrimentals = new List<KeyValuePair<ActorModel, DetrimentalModel>>();
            Codes = codes.Where(c => c.Type == ChatcodeType.Detrimental).ToList();
            Handlers = new Dictionary<ChatcodeType, ChatcodeTypeHandler>
            {
                {ChatcodeType.Detrimental, _handleDetrimental}
            };

            _checkDetrimentals = _clock.CreateTimer();
            _checkDetrimentals.Elapsed += CheckDetrimentals;
        }

        /// <summary>
        /// Timer event that updates all detrimentals that is done
        /// </summary>
        private void CheckDetrimentals()
        {
            _checkDetrimentals.Stop();
            _checkDetrimentalsNext = null;
            DateTime? nextCheck = null;
            var detrimentals = _activeDetrimentals.ToList();
            for (var i = 0; i < detrimentals.Count; i++)
            {
                // Ignore detrimentals that do not have last utc set
                if (detrimentals[i].Value.LastUtc.HasValue == false)
                    continue;

                // Detrimentals that has yet to be done
                if (detrimentals[i].Value.LastUtc > _clock.UtcNow)
                {
                    if (nextCheck == null || detrimentals[i].Value.LastUtc < nextCheck)
                        nextCheck = detrimentals[i].Value.LastUtc;
                    continue;
                }

                // Detrimentals that is done
                detrimentals[i].Value.IsActive = false;
                detrimentals[i].Key.UpdateStat(detrimentals[i].Value);
                _activeDetrimentals.Remove(detrimentals[i]);
            }

            if (nextCheck != null)
            {
                _checkDetrimentalsNext = nextCheck;
                _checkDetrimentals.Interval = Math.Max(1, (_checkDetrimentalsNext.Value - _clock.UtcNow).TotalMilliseconds);
                _checkDetrimentals.Start();
            }
        }

        /// <summary>
        /// Detrimental lose is marked as "Beneficial" in chat codes. so this method can be called from <see cref="BeneficialParse" />
        /// </summary>
        /// <param name="model">Containing information about detrimental</param>
        /// <param name="target">Information about the one who loses the effect</param>
        private void HandleDetrimentalLose(DetrimentalModel model, ActorModel target)
        {
            if (target == null || model == null)
                return;

            var active = _activeDetrimentals.FirstOrDefault(k => k.Value.Target == target.Name && k.Value.Name == model.Name && k.Value.IsActive);
            if (active.Key == null || active.Value == null)
                return;

            active.Value.LastUtc = model.TimeUtc;
            active.Value.IsActive = false;

            UpdateDetrimetnalTimer(model.TimeUtc);
        }

        /// <summary>
        /// If given time is before next detrimental check, then reset the timer so we check when <see ref="timeUtc" /> is up
        /// </summary>
        /// <param name="timeUtc">given time in utc</param>
        private void UpdateDetrimetnalTimer(DateTime timeUtc)
        {
            if (_checkDetrimentalsNext == null || _checkDetrimentalsNext > timeUtc)
            {
                _checkDetrimentals.Stop();
                _checkDetrimentals.Interval = Math.Min(1, (timeUtc - _clock.UtcNow).TotalMilliseconds);
                _checkDetrimentals.Start();
            }
        }

        private void HandleDetrimental(ChatCodes activeCode, Group group, Match match, ChatLogItem item)
        {
            var (model, source, target) = ToModel(match, item, group);
            if (match.Groups["loses"].Success)
            {
                HandleDetrimentalLose(model, target);
                return;
            }

            target?.Detrimentals?.Add(model);
            StoreDamage(model);
            if (source != null)
            {
                _activeDetrimentals.Add(new KeyValuePair<ActorModel, DetrimentalModel>(source, model));
                if (model.LastUtc.HasValue)
                    UpdateDetrimetnalTimer(model.LastUtc.Value);
            }
        }

        /// <summary>
        /// Converts given regex match to an <see cref="DamageModel" />
        /// </summary>
        /// <param name="r">regex match</param>
        /// <param name="item">the actual chat log item</param>
        /// <param name="group">chatcodes group</param>
        /// <returns>an <see cref="DamaModel" /> based on input parameters</returns>
        private (DetrimentalModel model, ActorModel source, ActorModel target) ToModel(Match r, ChatLogItem item, Group group)
        {
            var target = r.Groups["target"].Value;
            var status = r.Groups["status"].Value;
            var code = item.Code;
            var source = string.Empty;
            var action = string.Empty;

            if (_actions.TryGet(group.Subject, out var la))
            {
                source = la.Name;
                action = la.Action;
            }

            target = CleanName(target);

            var actorSource = string.IsNullOrEmpty(source) ? null : _actors.GetModel(source, group.Subject);
            var actorTarget = string.IsNullOrEmpty(target) ? null : _actors.GetModel(target, group.Subject, group.Direction);

            var model = _detrimentalFactory.GetModel(status, item.TimeStamp.ToString("yyyy-MM-dd HH:mm:ss"), _clock.UtcNow,
                                                    source, target, code, group.Direction.ToString(), group.Subject.ToString(),
                                                    _actions.Factory);

            return (model, actorSource, actorTarget);
        }

        private ChatcodeTypeHandler _handleDetrimental => new ChatcodeTypeHandler(
            ChatcodeType.Detrimental,
            new RegExDictionary(
                RegExDictionary.DamagePlayerAction,
                RegExDictionary.DetrimentalPlayer,
                RegExDictionary.DetrimentalPlayerRecovers,
                RegExDictionary.DetrimentalMonsterRecovers
            ),
            HandleDetrimental
        );
    }
}