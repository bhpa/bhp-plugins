﻿using Microsoft.AspNetCore.Http;
using Bhp.IO.Data.LevelDB;
using Bhp.IO.Json;
using Bhp.Network.RPC;
using System.IO;

namespace Bhp.Plugins
{
    public class LogReader : Plugin, IRpcPlugin
    {
        private readonly DB db;

        public override string Name => "ApplicationLogs";

        public LogReader()
        {
            this.db = DB.Open(Path.GetFullPath(Settings.Default.Path), new Options { CreateIfMissing = true });
            System.ActorSystem.ActorOf(Logger.Props(System.Blockchain, db));
        }

        public override void Configure()
        {
            Settings.Load(GetConfiguration());
        }

        public JObject OnProcess(HttpContext context, string method, JArray _params)
        {
            if (method != "getapplicationlog") return null;
            UInt256 hash = UInt256.Parse(_params[0].AsString());
            if (!db.TryGet(ReadOptions.Default, hash.ToArray(), out Slice value))
                throw new RpcException(-100, "Unknown transaction");
            return JObject.Parse(value.ToString());
        }
    }
}
