﻿using System.Collections.Generic;
using System.Linq;
using Bloop.Plugin;

namespace Bloop.Core.Plugin.QueryDispatcher
{
    public class GenericQueryDispatcher : BaseQueryDispatcher
    {
        protected override List<PluginPair> GetPlugins(Query query)
        {
            return PluginManager.AllPlugins.Where(o => PluginManager.IsGenericPlugin(o.Metadata)).ToList();
        }
    }
}