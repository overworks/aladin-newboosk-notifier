using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreTweet;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Table;

namespace Mh.Functions.AladinNewBookNotifier
{
    public static class WeeklyTimerTrigger
    {
        private static async Task<List<Status>> FetchLastTweetList(
            Tokens tokens,
            long userId,
            DateTimeOffset dateTimeOffset,
            TimeSpan timeRange,
            CancellationToken cancellationToken)
        {
            var list = new List<Status>();
            long? max_id = null;
            bool broken = false;
            
            do
            {
                if (cancellationToken.IsCancellationRequested) return null;
                var statuses = await tokens.Statuses.UserTimelineAsync(userId, max_id: max_id, trim_user: true, cancellationToken: cancellationToken);

                foreach (var status in statuses)
                {
                    var timeSpan = dateTimeOffset - status.CreatedAt;
                    if (timeSpan > timeRange)
                    {
                        broken = true;
                        break;
                    }
                    list.Add(status);
                }

                var lastElem = statuses[statuses.Count - 1];
                max_id = lastElem.Id - 1;
            }
            while (!broken);

            return list;
        }

        private static async Task TweetWeeklyReportAsync(
            DateTimeOffset dateTimeOffset,
            CloudTable credentialsTable,
            string categoryId,
            CancellationToken cancellationToken)
        {
            var tokens = await Twitter.Utils.CreateTokenAsync(credentialsTable, categoryId);
            if (cancellationToken.IsCancellationRequested) return;
            var user = await tokens.Account.VerifyCredentialsAsync(false, true, false, cancellationToken: cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            // 1주일 전의 것까지 가져오기.
            var list = await FetchLastTweetList(tokens, user.Id.Value, dateTimeOffset, TimeSpan.FromDays(7), cancellationToken);
            if (list == null || list.Count == 0) return;
            if (cancellationToken.IsCancellationRequested) return;
            
            // 같은 수가 여러개 있을수도 있는데... 근데 인용은 하나만 되니까 그냥 가자.
            list.Sort((l, r) => {
                if (l.RetweetCount > r.RetweetCount) return -1;
                if (l.RetweetCount < r.RetweetCount) return 1;
                if (l.FavoriteCount > r.FavoriteCount) return -1;
                if (l.FavoriteCount < r.FavoriteCount) return 1;
                return 0;
            });
            var mostRetweeted = (list.Count > 0 && list[0].RetweetCount > 0) ? list[0] : null;

            list.Sort((l, r) => {
                if (l.FavoriteCount > r.FavoriteCount) return -1;
                if (l.FavoriteCount < r.FavoriteCount) return 1;
                if (l.RetweetCount > r.RetweetCount) return -1;
                if (l.RetweetCount < r.RetweetCount) return 1;
                return 0;
            });
            var mostFavorated = (list.Count > 0 && list[0].FavoriteCount > 0) ? list[0] : null;
            
            StatusResponse response = null;
            if (mostRetweeted != null)
            {
                string permalink = $"https://twitter.com/{user.ScreenName}/status/{mostRetweeted.Id.ToString()}";
                response = await tokens.Statuses.UpdateAsync($"지난 한 주간 가장 많이 리트윗된 도서 ({mostRetweeted.RetweetCount}회)", attachment_url: permalink, cancellationToken: cancellationToken);
            }
            if (cancellationToken.IsCancellationRequested) return;

            if (mostFavorated != null)
            {
                string permalink = $"https://twitter.com/{user.ScreenName}/status/{mostFavorated.Id.ToString()}";
                long? replyStatusId = null;
                if (response != null) replyStatusId = response.Id;
                await tokens.Statuses.UpdateAsync($"지난 한 주간 가장 많이 좋아요 표시된 도서 ({mostRetweeted.FavoriteCount}회)", replyStatusId, attachment_url: permalink, cancellationToken: cancellationToken);
            }
            if (cancellationToken.IsCancellationRequested) return;
        }

        [FunctionName("WeeklyTimerTrigger")]
        public static async Task Run(
            [TimerTrigger("0 0 0 * * Sun")] TimerInfo myTimer,
            [Table("Credentials", "Twitter")] CloudTable credentialsTable,
            [Table("LineAccount")] CloudTable lineAccountTable,
            ILogger log,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.Now;

            log.LogInformation($"C# Weekly timer trigger function executed at: {now.ToString()}");

            var comicsTask = TweetWeeklyReportAsync(now, credentialsTable, Aladin.Const.CategoryID_Comics, cancellationToken);
            var lnovelTask = TweetWeeklyReportAsync(now, credentialsTable, Aladin.Const.CategoryID_LNovel, cancellationToken);
            var itbookTask = TweetWeeklyReportAsync(now, credentialsTable, Aladin.Const.CategoryID_ITBook, cancellationToken);

            await Task.WhenAll(comicsTask, lnovelTask, itbookTask);
        }
    }
}