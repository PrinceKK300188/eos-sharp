﻿using EosSharp.Api.v1;
using EosSharp.Helpers;
using EosSharp.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EosSharp
{
    public class Eos
    {
        private EosConfigurator EosConfig { get; set; }
        private EosApi Api { get; set; }
        private AbiSerializationProvider AbiSerializer { get; set; }

        public Eos(EosConfigurator config)
        {
            EosConfig = config ?? throw new ArgumentNullException("config");
            Api = new EosApi(EosConfig);
            AbiSerializer = new AbiSerializationProvider(Api);
        }

        #region Api Methods

        public Task<GetInfoResponse> GetInfo()
        {
            return Api.GetInfo();
        }

        public Task<GetAccountResponse> GetAccount(string accountName)
        {
            return Api.GetAccount(new GetAccountRequest()
            {
                AccountName = accountName
            });
        }

        public Task<GetCodeResponse> GetCode(string accountName, bool codeAsWasm)
        {
            return Api.GetCode(new GetCodeRequest()
            {
                AccountName = accountName,
                CodeAsWasm = codeAsWasm
            });
        }

        public async Task<Abi> GetAbi(string accountName)
        {
            return (await Api.GetAbi(new GetAbiRequest()
            {
                AccountName = accountName
            })).Abi;
        }

        public Task<GetRawCodeAndAbiResponse> GetRawCodeAndAbi(string accountName)
        {
            return Api.GetRawCodeAndAbi(new GetRawCodeAndAbiRequest()
            {
                AccountName = accountName
            });
        }

        public async Task<string> AbiJsonToBin(string code, string action, object data)
        {
            return (await Api.AbiJsonToBin(new AbiJsonToBinRequest()
            {
                Code = code,
                Action = action,
                Args = data
            })).Binargs;
        }

        public async Task<object> AbiBinToJson(string code, string action, string data)
        {
            return (await Api.AbiBinToJson(new AbiBinToJsonRequest()
            {
                Code = code,
                Action = action,
                Binargs = data
            })).Args;
        }

        public async Task<List<string>> GetRequiredKeys(List<string> availableKeys, Transaction trx)
        {
            int actionIndex = 0;
            var abiSerializer = new AbiSerializationProvider(Api);
            var abiResponses = await abiSerializer.GetTransactionAbis(trx);

            foreach (var action in trx.ContextFreeActions)
            {
                action.Data = SerializationHelper.ByteArrayToHexString(abiSerializer.SerializeActionData(action, abiResponses[actionIndex++]));
            }

            foreach (var action in trx.Actions)
            {
                action.Data = SerializationHelper.ByteArrayToHexString(abiSerializer.SerializeActionData(action, abiResponses[actionIndex++]));
            }

            return (await Api.GetRequiredKeys(new GetRequiredKeysRequest()
            {
                AvailableKeys = availableKeys,
                Transaction = trx
            })).RequiredKeys;
        }

        public Task<GetBlockResponse> GetBlock(string blockNumOrId)
        {
            return Api.GetBlock(new GetBlockRequest()
            {
                BlockNumOrId = blockNumOrId
            });
        }

        public Task<GetBlockHeaderStateResponse> GetBlockHeaderState(string blockNumOrId)
        {
            return Api.GetBlockHeaderState(new GetBlockHeaderStateRequest()
            {
                BlockNumOrId = blockNumOrId
            });
        }

        public async Task<GetTableRowsResponse<TRowType>> GetTableRows<TRowType>(GetTableRowsRequest request)
        {
            if(request.Json.GetValueOrDefault())
            {
                return await Api.GetTableRows<TRowType>(request);
            }
            else
            {
                var apiResult = await Api.GetTableRows(request);
                var result = new GetTableRowsResponse<TRowType>()
                {
                    More = apiResult.More
                };

                var unpackedRows = new List<TRowType>();

                var abi = await AbiSerializer.GetAbi(request.Code);
                var table = abi.Tables.First(t => t.Name == request.Table);

                foreach (var rowData in apiResult.Rows)
                {
                    unpackedRows.Add(AbiSerializer.DeserializeStructData<TRowType>(table.Type, (string)rowData, abi));
                }

                result.Rows = unpackedRows;
                return result;
            }
        }

        public async Task<GetTableRowsResponse> GetTableRows(GetTableRowsRequest request)
        {
            var result = await Api.GetTableRows(request);

            if(!request.Json.GetValueOrDefault())
            {
                var unpackedRows = new List<object>();

                var abi = await AbiSerializer.GetAbi(request.Code);
                var table = abi.Tables.First(t => t.Name == request.Table);

                foreach(var rowData in result.Rows)
                {
                    unpackedRows.Add(AbiSerializer.DeserializeStructData(table.Type, (string)rowData, abi));
                }

                result.Rows = unpackedRows;
            }

            return result;
        }

        public async Task<List<string>> GetCurrencyBalance(string code, string account, string symbol)
        {
            return (await Api.GetCurrencyBalance(new GetCurrencyBalanceRequest()
            {
                Code = code,
                Account = account,
                Symbol = symbol
            })).Assets;
        }

        public async Task<Dictionary<string, CurrencyStat>> GetCurrencyStats(string code, string symbol)
        {
            return (await Api.GetCurrencyStats(new GetCurrencyStatsRequest()
            {
                Code = code,
                Symbol = symbol
            })).Stats;
        }

        public async Task<GetProducersResponse> GetProducers(GetProducersRequest request)
        {
            var result = await Api.GetProducers(request);

            if (!request.Json.GetValueOrDefault())
            {
                var unpackedRows = new List<object>();

                foreach (var rowData in result.Rows)
                {
                    unpackedRows.Add(AbiSerializer.DeserializeType<Producer>((string)rowData));
                }

                result.Rows = unpackedRows;
            }

            return result;
        }

        public Task<GetProducerScheduleResponse> GetProducerSchedule()
        {
            return Api.GetProducerSchedule();
        }

        public async Task<GetScheduledTransactionsResponse> GetScheduledTransactions(GetScheduledTransactionsRequest request)
        {
            var result = await Api.GetScheduledTransactions(request);

            if (!request.Json.GetValueOrDefault())
            {
                foreach (var trx in result.Transactions)
                {
                    try
                    {
                        trx.Transaction = await AbiSerializer.DeserializePackedTransaction((string)trx.Transaction);
                    }
                    catch (Exception) {
                        //ignore transactions with invalid abi's
                    }
                }
            }

            return result;
        }

        public async Task<string> CreateTransaction(Transaction trx)
        {
            if (EosConfig.SignProvider == null)
                throw new ArgumentNullException("SignProvider");

            GetInfoResponse getInfoResult = null;
            string chainId = EosConfig.ChainId;

            if (string.IsNullOrWhiteSpace(chainId))
            {
                getInfoResult = await Api.GetInfo();
                chainId = getInfoResult.ChainId;
            }

            if (trx.Expiration == DateTime.MinValue ||
               trx.RefBlockNum == 0 ||
               trx.RefBlockPrefix == 0)
            {
                if(getInfoResult == null)
                    getInfoResult = await Api.GetInfo();

                var getBlockResult = await Api.GetBlock(new GetBlockRequest()
                {
                    BlockNumOrId = getInfoResult.LastIrreversibleBlockNum.GetValueOrDefault().ToString()
                });

                trx.Expiration = getInfoResult.HeadBlockTime.Value.AddSeconds(EosConfig.ExpireSeconds);
                trx.RefBlockNum = (UInt16)(getInfoResult.LastIrreversibleBlockNum.Value & 0xFFFF);
                trx.RefBlockPrefix = getBlockResult.RefBlockPrefix;
            }

            var packedTrx = await AbiSerializer.SerializePackedTransaction(trx);

            var availableKeys = await EosConfig.SignProvider.GetAvailableKeys();
            var requiredKeys = await GetRequiredKeys(availableKeys.ToList(), trx);
            var signatures = await EosConfig.SignProvider.Sign(chainId, requiredKeys, packedTrx);

            var result = await Api.PushTransaction(new PushTransactionRequest()
            {
                Signatures = signatures.ToArray(),
                Compression = 0,
                PackedContextFreeData = "",
                PackedTrx = SerializationHelper.ByteArrayToHexString(packedTrx)
            });

            return result.TransactionId;
        }

        public Task<GetActionsResponse> GetActions(string accountName, Int32? pos = null, Int32? offset = null)
        {
            return Api.GetActions(new GetActionsRequest()
            {
                AccountName = accountName,
                Pos = pos,
                Offset = offset
            });
        }

        public Task<GetTransactionResponse> GetTransaction(string transactionId)
        {
            return Api.GetTransaction(new GetTransactionRequest()
            {
                Id = transactionId
            });
        }

        public async Task<List<string>> GetKeyAccounts(string publicKey)
        {
            return (await Api.GetKeyAccounts(new GetKeyAccountsRequest()
            {
                PublicKey = publicKey
            })).AccountNames;
        }

        public async Task<List<string>> GetControlledAccounts(string accountName)
        {
            return (await Api.GetControlledAccounts(new GetControlledAccountsRequest()
            {
                ControllingAccount = accountName
            })).ControlledAccounts;
        }

        #endregion
    }
}
