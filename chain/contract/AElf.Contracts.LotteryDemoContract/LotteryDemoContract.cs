using System.Collections.Generic;
using System.Linq;
using AElf.Contracts.MultiToken;
using AElf.Sdk.CSharp;
using AElf.Types;
using Google.Protobuf.WellKnownTypes;

namespace AElf.Contracts.LotteryDemoContract
{
    public partial class LotteryDemoContract : LotteryDemoContractContainer.LotteryDemoContractBase
    {
        private const long Decimals = 100_000_000;
        private const int Price = 100;
        private const long Lag = 80;

        public override Empty InitializeLotteryDemoContract(InitializeLotteryDemoContractInput input)
        {
            Assert(State.TokenSymbol.Value == null, "Already initialized");
            State.TokenSymbol.Value = input.TokenSymbol;
            State.CurrentLotteryId.Value = 0;
            State.CurrentPeriod.Value = 0;
            State.Admin.Value = Context.Sender;
            State.TokenContract.Value =
                Context.GetContractAddressByName(SmartContractConstants.TokenContractSystemName);
            State.AEDPoSContract.Value =
                Context.GetContractAddressByName(SmartContractConstants.ConsensusContractSystemName);

            return new Empty();
        }

        public override Empty Buy(BuyInput input)
        {
            Assert(input.Amount < 100, "太jb多了");
            Assert(input.Amount > 0, "好歹买一个");

            //初始化
            State.OwnerToLotteries[Context.Sender] =
                State.OwnerToLotteries[Context.Sender] ?? new LotteryList {Ids = { }};

            State.TokenContract.TransferToContract.Send(new TransferToContractInput
            {
                Symbol = State.TokenSymbol.Value,
                Amount = Decimals.Mul(Price).Mul(input.Amount)
            });

            for (var i = 0; i < input.Amount; i++)
            {
                var hash = GetHashToken(i);
                var lottery = new Lottery
                {
                    Id = State.CurrentLotteryId.Value,
                    TokenHash = hash,
                    Owner = Context.Sender,
                    Level = 0,
                    Block = Context.CurrentHeight,
                };
                State.Lotteries[State.CurrentLotteryId.Value] = lottery;
                State.LotteryToId[hash] = State.CurrentLotteryId.Value;
                State.OwnerToLotteries[Context.Sender].Ids.Add(State.CurrentLotteryId.Value);
                State.LotteryToOwner[hash] = Context.Sender;
                State.CurrentLotteryId.Value += 1;
            }

            return new Empty();
        }

        public override Empty Draw(DrawInput input)
        {
            Assert(Context.Sender == State.Admin.Value, "No permission to draw!");
            Assert(State.Periods[State.CurrentPeriod.Value].RandomHash == Hash.Empty, "Latest period already drawn");

            var randomHash = State.AEDPoSContract.GetRandomHash.Call(new SInt64Value
            {
                Value = State.Periods[State.CurrentPeriod.Value].BlockNumber,
            });

            State.Periods[State.CurrentPeriod.Value].RandomHash = randomHash;

            //根据随机数处理彩票
            DealWithLotteries(input);

            return new Empty();
        }

        public override Empty PrepareDraw(Empty input)
        {
            Assert(Context.Sender == State.Admin.Value, "No permission to prepare!");
            //检查没有未开的奖
            Assert(
                State.Periods[State.CurrentPeriod.Value] == null ||
                State.Periods[State.CurrentPeriod.Value].RandomHash != Hash.Empty,
                "There's still one period not finished");

            State.CurrentPeriod.Value = State.CurrentPeriod.Value.Add(1);
            State.Periods[State.CurrentPeriod.Value] = new PeriodBody
            {
                Id = State.CurrentPeriod.Value,
                BlockNumber = Context.CurrentHeight + Lag,
                RandomHash = Hash.Empty
            };

            //初始化
            State.PeriodToResultsList[State.CurrentPeriod.Value] = new RewardResultsList();

            return new Empty();
        }

        public override Empty TakeReward(TakeRewardInput input)
        {
            Assert(State.LotteryToOwner[input.RandomHash] == Context.Sender, "You can only take your own lottery.");
            Assert(State.Lotteries[State.LotteryToId[input.RandomHash]].Level != 0, "No reward.");
            Assert(State.LotteryToData[input.RandomHash] == null, "Already took before.");

            State.LotteryToData[input.RandomHash].Value = input.Data;

            return new Empty();
        }

        public override GetRewardResultOutput GetRewardResult(GetRewardResultInput input)
        {
            var results = State.PeriodToResultsList[input.Period];
            Assert(results != null, "No results of this period.");
            var randomHash = State.Periods[input.Period].RandomHash;
            // ReSharper disable once PossibleNullReferenceException
            var lotteries = results.RewardResults.Select(result => State.Lotteries[result.LotteryId]).ToList();

            return new GetRewardResultOutput
            {
                Period = input.Period,
                RandomHash = randomHash,
                RewardLotteries = {lotteries}
            };
        }

        public override GetLotteriesOutput GetLotteries(Empty input)
        {
            return new GetLotteriesOutput
            {
                Lotteries =
                {
                    State.OwnerToLotteries[Context.Sender].Ids.Select(id => State.Lotteries[id] ?? new Lottery())
                }
            };
        }

        private void DealWithLotteries(DrawInput input)
        {
            var pool = new List<ulong>();
            ulong category = 1; //category为奖品   比如LevelsCount=[2,0,3,6,0] category从1到5  category为1的奖品数为2，2的奖品数为0，以此类推
            var randomHash = State.Periods[State.CurrentPeriod.Value].RandomHash;

            //把未中奖的lottery放入pool
            for (ulong i = 0; i < State.CurrentLotteryId.Value; i++)
            {
                if (State.Lotteries[i].Level == 0 &&
                    State.Lotteries[i].Block < State.Periods[State.CurrentPeriod.Value].BlockNumber)
                    pool.Add(i);
            }

            Assert(pool.Any(), "Available lottery not found.");

            var rewardResultsList = State.PeriodToResultsList[State.CurrentPeriod.Value];
            //按level进行抽奖，有不少变量强制转换，有安全隐患
            foreach (var count in input.LevelsCount)
            {
                var i = count;
                while (i > 0)
                {
                    var luckyIndex = randomHash.ToInt64() % pool.Count;
                    var luckyId = pool.Skip((int) luckyIndex).Take(1).First();
                    State.Lotteries[luckyId].Level = category;
                    rewardResultsList.RewardResults.Add(new RewardResult
                    {
                        LotteryId = luckyId
                    });

                    pool.Remove(luckyId);
                    //不断对自己hash产生新随机数
                    randomHash = Hash.FromByteArray(randomHash.ToByteArray());

                    i--;
                }

                category++;
            }

            State.PeriodToResultsList[State.CurrentPeriod.Value] = rewardResultsList;
        }

        private Hash GetHashToken(int index)
        {
            var hash = Hash.FromString(Context.Sender + index.ToString() + Context.CurrentHeight);
            Assert(State.LotteryToOwner[hash] == null, "One sender only can buy once at the same height.");
            return hash;
        }
    }
}