using Akka.Actor;
using Microsoft.AspNetCore.Http;
using Bhp.IO;
using Bhp.IO.Json;
using Bhp.Ledger;
using Bhp.Network.P2P;
using Bhp.Network.P2P.Payloads;
using Bhp.Network.RPC;
using Bhp.Persistence;
using Bhp.SmartContract;
using Bhp.Wallets;
using Bhp.Wallets.BRC6;
using System.Collections.Generic;
using System.Linq;
using Bhp.BhpExtensions.RPC;

namespace Bhp.Plugins
{
    public class RpcWallet : Plugin, IRpcPlugin
    {
        private Wallet Wallet => System.RpcServer.Wallet;
        private RpcExtension rpcExtension => System.RpcServer.rpcExtension;
        private BhpSystem system => System.RpcServer.system;

        public override void Configure()
        {
            Settings.Load(GetConfiguration());
        }

        public void PreProcess(HttpContext context, string method, JArray _params)
        {
        }

        public JObject OnProcess(HttpContext context, string method, JArray _params)
        {
            switch (method)
            {
                case "claimgas":
                    return ClaimGas(_params);
                case "dumpprivkey":
                    return DumpPrivKey(_params);
                case "getbalance":
                    return GetBalance(_params);
                case "getnewaddress":
                    return GetNewAddress();
                case "getunclaimedgas":
                    return GetUnclaimedGas();
                case "getwalletheight":
                    return GetWalletHeight();
                case "importprivkey":
                    return ImportPrivKey(_params);
                case "listaddress":
                    return ListAddress();
                case "sendfrom":
                    return SendFrom(_params);
                case "sendmany":
                    return SendMany(_params);
                case "sendtoaddress":
                    return SendToAddress(_params);
                //by bhp
                //case "claimgas":
                //    return ClaimGas();
                case "showgas":
                    return ShowGas();
                case "getutxos":
                    return GetUtxos(_params);
                case "sendissuetransaction":
                    return SendIssueTransaction(_params);
                case "gettransactiondata":
                    return SendToAddress(_params, true);
                default:
                    return null;
            }
        }

        public void PostProcess(HttpContext context, string method, JArray _params, JObject result)
        {
            switch (method)
            {
                case "invoke":
                case "invokefunction":
                case "invokescript":
                    ProcessInvoke(result);
                    break;
            }
        }

        private void ProcessInvoke(JObject result)
        {
            if (Wallet != null)
            {
                InvocationTransaction tx = new InvocationTransaction
                {
                    Version = 1,
                    Script = result["script"].AsString().HexToBytes(),
                    Gas = Fixed8.Parse(result["gas_consumed"].AsString())
                };
                tx.Gas -= Fixed8.FromDecimal(10);
                if (tx.Gas < Fixed8.Zero) tx.Gas = Fixed8.Zero;
                tx.Gas = tx.Gas.Ceiling();
                tx = Wallet.MakeTransaction(tx);
                if (tx != null)
                {
                    ContractParametersContext context = new ContractParametersContext(tx);
                    Wallet.Sign(context);
                    if (context.Completed)
                        tx.Witnesses = context.GetWitnesses();
                    else
                        tx = null;
                }
                result["tx"] = tx?.ToArray().ToHexString();
            }
        }

        private JObject ClaimGas(JArray _params)
        {
            WalletVerify();
            UInt160 to = _params.Count >= 1 ? _params[0].AsString().ToScriptHash() : null;
            const int MAX_CLAIMS_AMOUNT = 50;
            CoinReference[] claims = Wallet.GetUnclaimedCoins().Select(p => p.Reference).ToArray();
            if (claims.Length == 0)
                throw new RpcException(-300, "No gas to claim");
            ClaimTransaction tx;
            using (Snapshot snapshot = Blockchain.Singleton.GetSnapshot())
            {
                tx = new ClaimTransaction
                {
                    Claims = claims.Take(MAX_CLAIMS_AMOUNT).ToArray(),
                    Attributes = new TransactionAttribute[0],
                    Inputs = new CoinReference[0],
                    Outputs = new[]
                    {
                        new TransactionOutput
                        {
                            AssetId = Blockchain.UtilityToken.Hash,
                            Value = snapshot.CalculateBonus(claims.Take(MAX_CLAIMS_AMOUNT)),
                            ScriptHash = to ?? Wallet.GetChangeAddress()
                        }
                    }
                };
            }
            return SignAndShowResult(tx);
        }

        private JObject DumpPrivKey(JArray _params)
        {
            WalletVerify();
            UInt160 scriptHash = _params[0].AsString().ToScriptHash();
            WalletAccount account = Wallet.GetAccount(scriptHash);
            if(account is null)
                throw new RpcException(-100, "Unknown Address");
            return account?.GetKey().Export();
        }

        private JObject GetBalance(JArray _params)
        {
            WalletVerify();
            JObject json = new JObject();
            switch (UIntBase.Parse(_params[0].AsString()))
            {
                case UInt160 asset_id_160: //BRC-5 balance
                    json["balance"] = Wallet.GetAvailable(asset_id_160).ToString();
                    break;
                case UInt256 asset_id_256: //Global Assets balance
                    IEnumerable<Coin> coins = Wallet.GetCoins().Where(p => !p.State.HasFlag(CoinState.Spent) && p.Output.AssetId.Equals(asset_id_256));
                    json["balance"] = coins.Sum(p => p.Output.Value).ToString();
                    json["confirmed"] = coins.Where(p => p.State.HasFlag(CoinState.Confirmed)).Sum(p => p.Output.Value).ToString();
                    break;
            }
            return json;
        }

        private JObject GetNewAddress()
        {
            WalletVerify();
            WalletAccount account = Wallet.CreateAccount();
            if (Wallet is BRC6Wallet brc6)
                brc6.Save();
            return account.Address;
        }

        private JObject GetUnclaimedGas()
        {
            WalletVerify();
            using (Snapshot snapshot = Blockchain.Singleton.GetSnapshot())
            {
                uint height = snapshot.Height + 1;
                Fixed8 unavailable;
                try
                {
                    unavailable = snapshot.CalculateBonus(Wallet.FindUnspentCoins().Where(p => p.Output.AssetId.Equals(Blockchain.GoverningToken.Hash)).Select(p => p.Reference), height);
                }
                catch
                {
                    unavailable = Fixed8.Zero;
                }
                return new JObject
                {
                    ["available"] = snapshot.CalculateBonus(Wallet.GetUnclaimedCoins().Select(p => p.Reference)).ToString(),
                    ["unavailable"] = unavailable.ToString()
                };
            }
        }

        private JObject GetWalletHeight()
        {
            WalletVerify();
            return (Wallet.WalletHeight > 0) ? Wallet.WalletHeight - 1 : 0;
        }

        private JObject ImportPrivKey(JArray _params)
        {
            WalletVerify();
            string privkey = _params[0].AsString();
            WalletAccount account = Wallet.Import(privkey);
            if (Wallet is BRC6Wallet brc6wallet)
                brc6wallet.Save();
            return new JObject
            {
                ["address"] = account.Address,
                ["haskey"] = account.HasKey,
                ["label"] = account.Label,
                ["watchonly"] = account.WatchOnly
            };
        }

        private JObject ListAddress()
        {
            WalletVerify();
            return Wallet.GetAccounts().Select(p =>
            {
                JObject account = new JObject();
                account["address"] = p.Address;
                account["haskey"] = p.HasKey;
                account["label"] = p.Label;
                account["watchonly"] = p.WatchOnly;
                return account;
            }).ToArray();
        }

        private JObject SendFrom(JArray _params)
        {
            WalletVerify();
            UIntBase assetId = UIntBase.Parse(_params[0].AsString());
            AssetDescriptor descriptor = new AssetDescriptor(assetId);
            UInt160 from = _params[1].AsString().ToScriptHash();
            UInt160 to = _params[2].AsString().ToScriptHash();
            BigDecimal value = BigDecimal.Parse(_params[3].AsString(), descriptor.Decimals);
            if (value.Sign <= 0)
                throw new RpcException(-32602, "Invalid params");
            Fixed8 fee = _params.Count >= 5 ? Fixed8.Parse(_params[4].AsString()) : Fixed8.Zero;
            if (fee < Fixed8.Zero)
                throw new RpcException(-32602, "Invalid params");
            UInt160 change_address = _params.Count >= 6 ? _params[5].AsString().ToScriptHash() : null;
            Transaction tx = Wallet.MakeTransaction(null, new[]
            {
                new TransferOutput
                {
                    AssetId = assetId,
                                Value = value,
                                ScriptHash = to
                }
            }, from: from, change_address: change_address, fee: fee);
            return SignAndShowResult(tx);
        }

        private JObject SendMany(JArray _params)
        {
            WalletVerify();
            int to_start = 0;
            UInt160 from = null;
            if (_params[0] is JString)
            {
                from = _params[0].AsString().ToScriptHash();
                to_start = 1;
            }
            JArray to = (JArray)_params[to_start + 0];
            if (to.Count == 0)
                throw new RpcException(-32602, "Invalid params");
            TransferOutput[] outputs = new TransferOutput[to.Count];
            for (int i = 0; i < to.Count; i++)
            {
                UIntBase asset_id = UIntBase.Parse(to[i]["asset"].AsString());
                AssetDescriptor descriptor = new AssetDescriptor(asset_id);
                outputs[i] = new TransferOutput
                {
                    AssetId = asset_id,
                    Value = BigDecimal.Parse(to[i]["value"].AsString(), descriptor.Decimals),
                    ScriptHash = to[i]["address"].AsString().ToScriptHash()
                };
                if (outputs[i].Value.Sign <= 0)
                    throw new RpcException(-32602, "Invalid params");
            }
            Fixed8 fee = _params.Count >= to_start + 2 ? Fixed8.Parse(_params[to_start + 1].AsString()) : Fixed8.Zero;
            if (fee < Fixed8.Zero)
                throw new RpcException(-32602, "Invalid params");
            UInt160 change_address = _params.Count >= to_start + 3 ? _params[to_start + 2].AsString().ToScriptHash() : null;
            Transaction tx = Wallet.MakeTransaction(null, outputs, from: from, change_address: change_address, fee: fee);
            return SignAndShowResult(tx);
        }

        public JObject SendToAddress(JArray _params, bool isHexString = false)
        {
            WalletVerify();
            UIntBase assetId = UIntBase.Parse(_params[0].AsString());
            AssetDescriptor descriptor = new AssetDescriptor(assetId);
            UInt160 scriptHash = _params[1].AsString().ToScriptHash();
            BigDecimal value = BigDecimal.Parse(_params[2].AsString(), descriptor.Decimals);
            if (value.Sign <= 0)
                throw new RpcException(-32602, "Invalid params");
            Fixed8 fee = _params.Count >= 4 ? Fixed8.Parse(_params[3].AsString()) : Fixed8.Zero;
            if (fee < Fixed8.Zero)
                throw new RpcException(-32602, "Invalid params");
            UInt160 change_address = _params.Count >= 5 ? _params[4].AsString().ToScriptHash() : null;
            Transaction tx = Wallet.MakeTransaction(null, new[]
            {
                new TransferOutput
                {
                    AssetId = assetId,
                    Value = value,
                    ScriptHash = scriptHash
                }
            }, change_address: change_address, fee: fee);
            return SignAndShowResult(tx, isHexString);
        }

        /*
        private JObject ClaimGas()
        {
            WalletVerify();
            JObject json = new JObject();
            RpcCoins coins = new RpcCoins(Wallet, system);
            ClaimTransaction[] txs = coins.ClaimAll();
            if (txs == null)
            {
                json["txs"] = new JArray();
            }
            else
            {
                json["txs"] = new JArray(txs.Select(p =>
                {
                    return p.ToJson();
                }));
            }
            return json;
        }
        */

        private JObject ShowGas()
        {
            WalletVerify();
            JObject json = new JObject();
            RpcCoins coins = new RpcCoins(Wallet, system);
            json["unavailable"] = coins.UnavailableBonus().ToString();
            json["available"] = coins.AvailableBonus().ToString();
            return json;
        }

        private JObject GetUtxos(JArray _params)
        {
            WalletVerify();
            JObject json = new JObject();
            //address,assetid
            UInt160 scriptHash = _params[0].AsString().ToScriptHash();
            IEnumerable<Coin> coins = Wallet.FindUnspentCoins();
            UInt256 assetId;
            if (_params.Count >= 2)
            {
                switch (_params[1].AsString())
                {
                    case "bhp":
                        assetId = Blockchain.GoverningToken.Hash;
                        break;
                    case "gas":
                        assetId = Blockchain.UtilityToken.Hash;
                        break;
                    default:
                        assetId = UInt256.Parse(_params[1].AsString());
                        break;
                }
            }
            else
            {
                assetId = Blockchain.GoverningToken.Hash;
            }
            coins = coins.Where(p => p.Output.AssetId.Equals(assetId) && p.Output.ScriptHash.Equals(scriptHash));

            //json["utxos"] = new JObject();
            Coin[] coins_array = coins.ToArray();
            //const int MAX_SHOW = 100;

            json["utxos"] = new JArray(coins_array.Select(p =>
            {
                return p.Reference.ToJson();
            }));

            return json;
        }

        private JObject SendIssueTransaction(JArray _params)
        {
            WalletVerify();
            UInt256 asset_id = UInt256.Parse(_params[0].AsString());
            JArray to = (JArray)_params[1];
            if (to.Count == 0)
                throw new RpcException(-32602, "Invalid params");
            TransactionOutput[] outputs = new TransactionOutput[to.Count];
            for (int i = 0; i < to.Count; i++)
            {
                AssetDescriptor descriptor = new AssetDescriptor(asset_id);
                outputs[i] = new TransactionOutput
                {
                    AssetId = asset_id,
                    Value = Fixed8.Parse(to[i]["value"].AsString()),
                    ScriptHash = to[i]["address"].AsString().ToScriptHash()
                };
            }
            IssueTransaction tx = Wallet.MakeTransaction(new IssueTransaction
            {
                Version = 1,
                Outputs = outputs
            }, fee: Fixed8.One);
            return SignAndShowResult(tx);
        }

        public void WalletVerify()
        {
            if (Wallet == null || rpcExtension.walletTimeLock.IsLocked())
                throw new RpcException(-400, "Access denied");
        }

        public JObject SignAndShowResult(Transaction tx, bool isHexString = false)
        {
            if (tx == null)
                throw new RpcException(-300, "Insufficient funds");
            ContractParametersContext context = new ContractParametersContext(tx);
            Wallet.Sign(context);
            if (context.Completed)
            {
                tx.Witnesses = context.GetWitnesses();

                if (tx.Size > Transaction.MaxTransactionSize)
                    throw new RpcException(-301, "The size of the free transaction must be less than 102400 bytes");

                Wallet.ApplyTransaction(tx);
                System.LocalNode.Tell(new LocalNode.Relay { Inventory = tx });

                if (isHexString)
                    return Bhp.IO.Helper.ToArray(tx).ToHexString();
                return tx.ToJson();
            }
            else
            {
                return context.ToJson();
            }
        }
    }
}