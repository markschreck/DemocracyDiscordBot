﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using DiscordBotBase;
using DiscordBotBase.CommandHandlers;
using Discord;
using Discord.WebSocket;
using Discord.Rest;
using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticToolkit;

namespace DemocracyDiscordBot.CommandHandlers
{
    /// <summary>
    /// Admin commands handler.
    /// </summary>
    public class AdminCommands : UserCommands
    {
        /// <summary>
        /// Admin command to start a new vote.
        /// </summary>
        public void CMD_CallVote(string[] cmds, IUserMessage message)
        {
            if (!DemocracyBot.IsAdmin(message.Author))
            {
                return;
            }
            if (message.Channel is IPrivateChannel)
            {
                SendErrorMessageReply(message, "Wrong Location", "Votes cannot be called from a private message.");
                return;
            }
            if (cmds.Length < 4)
            {
                SendErrorMessageReply(message, "Invalid Input", "Input does not look like it can possibly be valid. Use `!help` for usage information.");
                return;
            }
            string topicId = cmds[0].Replace("`", "").Trim();
            if (DemocracyBot.VoteTopicsSection.HasRootKeyLowered(topicId))
            {
                SendErrorMessageReply(message, "Topic Already Present", "That voting topic ID already exists. Pick a new one!");
                return;
            }
            StringBuilder topicTitle = new StringBuilder(100);
            topicTitle.Append(cmds[1].Replace("`", ""));
            int argId;
            for (argId = 2; argId < cmds.Length; argId++)
            {
                string arg = cmds[argId].Replace("`", "");
                if (arg.Trim() == "|")
                {
                    break;
                }
                topicTitle.Append(" ").Append(cmds[argId]);
            }
            List<string> choices = new List<string>(cmds.Length);
            StringBuilder currentChoice = new StringBuilder(100);
            for (argId++; argId < cmds.Length; argId++)
            {
                string arg = cmds[argId].Replace("`", "");
                if (arg.Trim() == "|")
                {
                    choices.Add(currentChoice.ToString().Trim());
                    currentChoice.Clear();
                    continue;
                }
                currentChoice.Append(" ").Append(cmds[argId]);
            }
            choices.Add(currentChoice.ToString().Trim());
            for (int i = 0; i < choices.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(choices[i]))
                {
                    choices.RemoveAt(i--);
                }
            }
            if (choices.IsEmpty())
            {
                SendErrorMessageReply(message, "Invalid Input", "No choices found.");
                return;
            }
            if (choices.Count == 1)
            {
                SendErrorMessageReply(message, "Insufficient Democracy", "Only 1 choice detected. Need at least 2.");
                return;
            }
            FDSSection newTopicSection = new FDSSection();
            newTopicSection.SetRoot("Topic", topicTitle.ToString());
            FDSSection choiceSection = new FDSSection();
            for (int i = 0; i < choices.Count; i++)
            {
                choiceSection.Set((i + 1).ToString(), choices[i]);
            }
            IUserMessage sentMessage = message.Channel.SendMessageAsync(embed: GetGenericPositiveMessageEmbed("Vote In Progress", "New Vote... Data inbound, please wait!")).Result;
            newTopicSection.SetRoot("channel_id", sentMessage.Channel.Id);
            newTopicSection.SetRoot("post_id", sentMessage.Id);
            newTopicSection.SetRoot("Choices", choiceSection);
            newTopicSection.SetRoot("user_results", new FDSSection());
            DemocracyBot.VoteTopicsSection.SetRoot(topicId, newTopicSection);
            DemocracyBot.Save();
            RefreshTopicData(topicId, newTopicSection, false);
        }

        /// <summary>
        /// Refreshes the topic data in the publicly visible counting post.
        /// </summary>
        public static void RefreshTopicData(string topicId, FDSSection topicSection, bool isClosed)
        {
            try
            {
                string topicTitle = topicSection.GetString("Topic");
                ulong channelId = topicSection.GetUlong("channel_id").Value;
                ulong postId = topicSection.GetUlong("post_id").Value;
                Console.WriteLine($"Try refresh of {topicId} via channel {channelId} at post {postId}.");
                StringBuilder choicesText = new StringBuilder(100);
                FDSSection choicesSection = topicSection.GetSection("Choices");
                foreach (string choice in choicesSection.GetRootKeys())
                {
                    choicesText.Append($"**{choice}**: `{choicesSection.GetString(choice)}`\n");
                }
                Embed embed = GetGenericPositiveMessageEmbed($"Vote For Topic **{topicId}**: {topicTitle}", $"Choices:\n{choicesText}\nVotes cast thus far: {topicSection.GetSection("user_results").Data.Count}\n\n"
                    + (isClosed ? "This vote is closed. Find the results below." : "DM this bot `!help` to cast your vote!"));
                IMessage message = (DiscordBotBaseHelper.CurrentBot.Client.GetChannel(channelId) as ITextChannel).GetMessageAsync(postId).Result;
                (message as IUserMessage).ModifyAsync(m => m.Embed = embed).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Topic data refresh for {topicId} failed: {ex}");
            }
        }

        /// <summary>
        /// Admin command to end an existing vote.
        /// </summary>
        public void CMD_EndVote(string[] cmds, IUserMessage message)
        {
            if (!DemocracyBot.IsAdmin(message.Author))
            {
                return;
            }
            FDSSection topicSection = VotingCommands.GetVoteTopicSection(cmds, message, out string topicName);
            if (topicSection == null)
            {
                return;
            }
            topicName = topicName.ToLowerFast();
            Console.WriteLine($"Trying to end vote for {topicName} at {StringConversionHelper.DateTimeToString(DateTimeOffset.UtcNow, true)}");
            FDSSection choicesSection = topicSection.GetSection("Choices");
            Bot.ConfigFile.Set("old_topics." + StringConversionHelper.DateTimeToString(DateTimeOffset.UtcNow, true).Replace(".", "_") + "_topic_" + topicName.Replace(".", "_"), topicSection);
            string realKey = DemocracyBot.VoteTopicsSection.Data.Keys.First(s => s.ToLowerFast() == topicName);
            DemocracyBot.VoteTopicsSection.Remove(realKey);
            RefreshTopicData(topicName, topicSection, true);
            FDSSection userResultsSection = topicSection.GetSection("user_results");
            tallyVotes(topicSection, choicesSection, userResultsSection, message, topicName, true);
        }

        public void CMD_VoteStatus(string[] cmds, IUserMessage message)
        {
            if (!DemocracyBot.IsAdmin(message.Author))
            {
                return;
            }
            FDSSection topicSection = VotingCommands.GetVoteTopicSection(cmds, message, out string topicName);
            if (topicSection == null)
            {
                return;
            }
            topicName = topicName.ToLowerFast();
            Console.WriteLine($"Trying get vote status for {topicName} at {StringConversionHelper.DateTimeToString(DateTimeOffset.UtcNow, true)}");
            FDSSection choicesSection = topicSection.GetSection("Choices");
            string realKey = DemocracyBot.VoteTopicsSection.Data.Keys.First(s => s.ToLowerFast() == topicName);
            RefreshTopicData(topicName, topicSection, false);
            FDSSection userResultsSection = topicSection.GetSection("user_results");
            tallyVotes(topicSection, choicesSection, userResultsSection, message, topicName, false);
        }

        private void tallyVotes(FDSSection topicSection, FDSSection choicesSection, FDSSection userResultsSection, IUserMessage message, string topicName, Boolean commitChanges) {
            if (userResultsSection == null || userResultsSection.GetRootKeys().IsEmpty())
            {
                SendGenericNegativeMessageReply(message, $"Vote For Topic {topicName} Failed", "No votes were cast.");
                if (commitChanges) DemocracyBot.Save();
                return;
            }
            List<List<string>> voteSets = new List<List<string>>(50);
            foreach (string userId in userResultsSection.GetRootKeys())
            {
                List<string> choices = userResultsSection.GetStringList(userId);
                if (choices != null && !choices.IsEmpty() && !(choices.Count == 1 && choices[0] == "none"))
                {
                    voteSets.Add(choices);
                }
            }
            if (voteSets.IsEmpty())
            {
                SendGenericNegativeMessageReply(message, $"Vote For Topic {topicName} Failed", "No votes were cast.");
                if (commitChanges) DemocracyBot.Save();
                return;
            }
            int usersWhoVotedTotal = voteSets.Count;
            int discards = 0;
            
            string gatherStats(string choice, string type, List<List<string>> placeVoteSets)
            {
                int numberHadFirst = 0;
                int positionTotal = 0;
                int numberHadAtAll = 0;
                foreach (string userId in userResultsSection.GetRootKeys())
                {
                    List<string> choices = userResultsSection.GetStringList(userId);
                    if (choices != null && !choices.IsEmpty())
                    {
                        int index = choices.IndexOf(choice);
                        if (index != -1)
                        {
                            numberHadAtAll++;
                            positionTotal += index + 1;
                            if (index == 0)
                            {
                                numberHadFirst++;
                            }
                        }
                    }
                }
                return $"Options that were discarded due to low support: {discards}\n"
                + $"Users whose votes were discarded due to supporting only unpopular options: {usersWhoVotedTotal - placeVoteSets.Count}\nUsers who listed the {type} first: {numberHadFirst}\n"
                + $"Users who listed the {type} at all: {numberHadAtAll}\nAverage ranking of the {type}: {positionTotal / (float)numberHadAtAll:0.0}";
            }

            Tuple<string, string> firstTuple = getRankWinner(voteSets.ConvertAll(new Converter<List<string>, List<string>>(x => x.ConvertAll(new Converter<string, string>(y => y.ToString())))));
            Tuple<string, string> secondTuple = getRankWinner(voteSets.ConvertAll(new Converter<List<string>, List<string>>(x => x.ConvertAll(new Converter<string, string>(y => y.ToString())))));
            Tuple<string, string> thirdTuple = getRankWinner(voteSets.ConvertAll(new Converter<List<string>, List<string>>(x => x.ConvertAll(new Converter<string, string>(y => y.ToString())))));
            Tuple<string, string> fourthTuple = getRankWinner(voteSets.ConvertAll(new Converter<List<string>, List<string>>(x => x.ConvertAll(new Converter<string, string>(y => y.ToString())))));
            Tuple<string, string> fifthTuple = getRankWinner(voteSets.ConvertAll(new Converter<List<string>, List<string>>(x => x.ConvertAll(new Converter<string, string>(y => y.ToString())))));
            Tuple<string, string> sixthTuple = getRankWinner(voteSets.ConvertAll(new Converter<List<string>, List<string>>(x => x.ConvertAll(new Converter<string, string>(y => y.ToString())))));

            Tuple<string, string> getRankWinner( List<List<string>> placeVoteSets ) {
                string topRank = "";
                string topRankStats = "";
                bool haveWinner = false;

                Dictionary<string, int> votesTracker = new Dictionary<string, int>(128);
                Dictionary<string, int> totalVotesTracker = new Dictionary<string, int>();

                discards = 0;

                foreach (List<string> voteSet in placeVoteSets)
                {
                    foreach (string singleVote in voteSet) {
                        if (!totalVotesTracker.TryGetValue(singleVote, out int singleVoteCount)) {
                            singleVoteCount = 0;
                        }
                        totalVotesTracker[singleVote] = singleVoteCount + 1;
                    }
                }                

                while (true)
                {
                    votesTracker.Clear();
                    foreach (List<string> voteSet in placeVoteSets)
                    {
                        string vote = voteSet[0]; //Only checking highest vote right now
                        if (!votesTracker.TryGetValue(vote, out int count))
                        {
                            count = 0;
                        }
                        votesTracker[vote] = count + 1;
                    }
                    if (votesTracker.Count == 0)
                    {
                        Console.WriteLine("Something went funky in vote counting... tracker is empty without a clear winner!");
                        break;
                    }
                    string best = placeVoteSets[0][0];
                    List<string> worstList = new List<string>();// placeVoteSets[0][0];
                    int bestCount = 0, worstCount = int.MaxValue;
                    foreach (KeyValuePair<string, int> voteResult in votesTracker)
                    {
                        if (voteResult.Value > bestCount)
                        {
                            best = voteResult.Key;
                            bestCount = voteResult.Value;
                        }
                        if (voteResult.Value < worstCount)
                        {
                            worstList = new List<string>() {voteResult.Key};
                            worstCount = voteResult.Value;
                        }
                        else if (voteResult.Value == worstCount) {
                            worstList.Add(voteResult.Key);
                        }
                    }
                    if (bestCount * 2 > placeVoteSets.Count)
                    {
                        if (!haveWinner)
                        {
                            topRank = best;
                            topRankStats = gatherStats(topRank, "winner", placeVoteSets);
                            haveWinner = true;
                            for (int i = 0; i < voteSets.Count; i++)
                            {
                                if (voteSets[i].Contains(topRank))
                                {
                                    voteSets[i].Remove(topRank);
                                    if (voteSets[i].IsEmpty())
                                    {
                                        voteSets.RemoveAt(i--);
                                    }
                                }
                            }
                            break;
                        }
                    }
                    string worst = totalVotesTracker.Where(x => worstList.Contains(x.Key)).Aggregate((l, r) => l.Value < r.Value ? l : r).Key;
                    for (int i = 0; i < placeVoteSets.Count; i++)
                    {
                        for (int j = 0; j < placeVoteSets[i].Count; j++)
                        if (placeVoteSets[i][j] == worst)
                        {
                            placeVoteSets[i].RemoveAt(j);
                        }
                    }
                    for (int i = 0; i< placeVoteSets.Count; i++) {
                        if (placeVoteSets[i].IsEmpty())
                        {
                            placeVoteSets.RemoveAt(i);
                            i--;
                        }
                    }
                    if (!haveWinner)
                    {
                        discards++;
                    }
                }
                return new Tuple<string, string>(topRank, topRankStats);
            }
            SendGenericPositiveMessageReply(message, $"Vote Results For **{topicName}: {topicSection.GetString("Topic")}**", $"**__Winner__**: **{firstTuple.Item1}**: `{choicesSection.GetString(firstTuple.Item1)}`"
                + $"\n\n**Stats:**\nUsers who voted, in total: {usersWhoVotedTotal}\n{firstTuple.Item2}\n\n"
                + $"**__Runner Up__**: **{secondTuple.Item1}**: `{choicesSection.GetString(secondTuple.Item1)}`\n**Stats For Runner Up**:\n{secondTuple.Item2}\n\n"
                + $"**__Thrid Place__**: **{thirdTuple.Item1}**: `{choicesSection.GetString(thirdTuple.Item1)}`\n**Stats For Third Place**:\n{thirdTuple.Item2}\n\n"
                + $"**__Fourth Place__**: **{fourthTuple.Item1}**: `{choicesSection.GetString(fourthTuple.Item1)}`\n**Stats For Fourth Place**:\n{fourthTuple.Item2}\n\n"
                + $"**__Fifth Place__**: **{fifthTuple.Item1}**: `{choicesSection.GetString(fifthTuple.Item1)}`\n**Stats For Fifth Place**:\n{fifthTuple.Item2}\n\n"
                + $"**__Sixth Place__**: **{sixthTuple.Item1}**: `{choicesSection.GetString(sixthTuple.Item1)}`\n**Stats For Sixth Place**:\n{sixthTuple.Item2}"
                );
            if (commitChanges) DemocracyBot.Save();
        }
    }
}
