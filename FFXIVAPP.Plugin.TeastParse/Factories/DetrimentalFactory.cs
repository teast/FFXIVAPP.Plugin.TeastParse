using System;
using FFXIVAPP.Plugin.TeastParse.Models;

namespace FFXIVAPP.Plugin.TeastParse.Factories
{
    internal interface IDetrimentalFactory
    {
        DetrimentalModel GetModel(string name, string timestamp, DateTime timeUtc, string source, string target, string chatCode, string direction, string subject);
    }

    internal class DetrimentalFactory: IDetrimentalFactory
    {
        public DetrimentalModel GetModel(string name, string timestamp, DateTime timeUtc, string source, string target, string chatCode, string direction, string subject)
        {
            // TODO: Use an "database" to lookup information about an detrimental here
            var lastUtc = (DateTime?)null;
            var actionName = name;
            var potency = 0;
            return new DetrimentalModel(name, actionName, potency, timestamp, timeUtc, lastUtc, source, target, chatCode, direction, subject);
        }
    }
}