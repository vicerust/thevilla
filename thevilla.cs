using Oxide.Core;
using Oxide.Ext.Discord;
using Oxide.Ext.Discord.Attributes;
using Oxide.Ext.Discord.DiscordEvents;
using Oxide.Ext.Discord.DiscordObjects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("The Villa", "Vice", "1.0.0")]
    [Description("the coolest rust plugin ever made")]
    public class TheVilla : RustPlugin
    {
        #region Definitions
        static TheVilla Instance;

        Dictionary<ulong, string> discordUsers = new Dictionary<ulong, string>();
        Dictionary<ulong, int> pinVerification = new Dictionary<ulong, int>();

        [DiscordClient] DiscordClient discordClient;
        Role DiscordVerifiedRole
        {
            get
            {
                string verificationRole = Config["Discord Verification Role"]?.ToString();
                return discordClient?.DiscordServer?.roles?.Where(x => x?.name?.ToLower() == verificationRole?.ToLower())?.FirstOrDefault();
            }
        }
        #endregion Definitions

        #region Server Hooks

        #region Init
        void OnServerInitialized()
        {
            Clan.LoadClans(Config["Clan Data Directory"].ToString());

            Clan.AllClans.ForEach((clan) =>
            {
                clan.Members.ForEach((member) =>
                {
                    clan.AddMemberToTeamUI(BasePlayer.FindByID(member));
                });
            });

            if ((bool)Config["Enable"] && Config["Discord Bot Api Key"].ToString().Length > 0)
                Discord.CreateClient(this, Config["Discord Bot Api Key"].ToString());
        }

        void Loaded()
        {
            Instance = this;
            LoadDiscordUsers();
            cmd.AddChatCommand(Config["Rust Verification Chat Command"].ToString(), this, "ChatCommand_DiscordVerify");
            cmd.AddChatCommand(Config["Team Chat Command"].ToString(), this, "ChatCommand_TeamChat");
            cmd.AddChatCommand(Config["Update Team Chat Command"].ToString(), this, "ChatCommand_UpdateTeam");
        }

        void Unload()
        {
            Clan.AllClans.Clear();
        }
        #endregion Init

        //Block Players from Leaving Teams
        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            if ((bool)arg?.cmd?.FullName?.Contains("relationshipmanager.") && !(bool)arg?.IsRcon)
            {
                if ((bool)Config["Prevent Team Commands For Clan Players"] == false) return null;

                BasePlayer initiator = arg.Player();
                if (initiator == null) return null;

                if (Clan.AllClans.Any(x => x.TeamUI.teamID == initiator.currentTeam)) return false;
            }
            return null;
        }

        //load default config
        protected override void LoadDefaultConfig()
        {
            Config["Prevent Team Commands For Clan Players"] = true;
            Config["Enable"] = true;
            Config["EnableTeamChatting"] = true;
            Config["Team Chat Command"] = "t";
            Config["Discord Bot Api Key"] = "hahanotmakingthismistakeagain";
            Config["Rust Verification Chat Command"] = "verify";
            Config["Discord Verification Command"] = "/verify";
            Config["Clan Data Directory"] = "Clans";
            Config["Update Team Chat Command"] = "updateteam";
        }

        // Disable Friendly Fire for Players in Teams
        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!(entity is BasePlayer)) return null;

            if (!(info.Initiator is BasePlayer)) return null;

            if ((info.Initiator as BasePlayer) == (entity as BasePlayer)) return null;

            if ((entity as BasePlayer).currentTeam == (info.Initiator as BasePlayer).currentTeam) return false;

            return null;
        }

        void OnPlayerInit(BasePlayer player)
        {
            Clan clan = Clan.AllClans.FirstOrDefault(x => x.Members.Contains(player.userID));
            if (clan == null)
            {
                Instance.PrintError("could not find player clan");
                return;
            }

            clan.AddMemberToTeamUI(player);
        }

        // Needs Heavy Testing (All Edge Cases)
        //
        // Door Sharing
        object CanUseLockedEntity(BasePlayer player, BaseLock baseLock)
        {
            // Check if player is in a team
            if (player.currentTeam == 0) return null;
            if (!Clan.AllClans.Any(x => x.TeamUI.teamID == player.currentTeam)) return null;

            // Check is the baseLock is a CodeLock
            if (!(baseLock is CodeLock)) return null;

            // Get the entity the codelock is parented to (eg. door)
            BaseEntity parent = baseLock.GetParentEntity();
            // Test that the parent is a stability entity and not a StorageContainer (Box)
            if (!(parent is StabilityEntity)) return null;

            // Check if player has building auth
            bool isBuildingAuthed = player.IsBuildingAuthed(new OBB(baseLock.transform.position, baseLock.transform.rotation, baseLock.transform.GetBounds()));
            // If Player has building Auth, Allow them to use CodeLock.
            if (isBuildingAuthed)
                return true;

            return null;
        }


        // Cupboard Sharing
        object OnCupboardClearList(BuildingPrivlidge privilege, BasePlayer player)
        { //refactor this whole thing
            return false;
        }


        #endregion Server Hooks

        //Get Clan ID String for Chat
        public string GetPlayerTitle(BasePlayer player)
        {
            return GetPlayerTitle(player.userID);
        }

        public string GetPlayerTitle(ulong playerId)
        {
            Clan clan = Clan.AllClans.FirstOrDefault(x => x.Members.Contains(playerId));
            if (clan == null)
                return null;

            return $"[{clan.ClanTag.ToUpper()}]";
        }

        #region Chat Commands
        //Team Chat with /t
        void ChatCommand_TeamChat(BasePlayer player, string command, string[] args)
        {
            if (!(bool)Config["EnableTeamChatting"] || args == null || args.Length == 0) return;

            if (args.Length >= 1)
            {
                RelationshipManager.PlayerTeam playersTeam = RelationshipManager.Instance.FindTeam(player.currentTeam);
                string joinedMessage = string.Join(" ", args);
                string playerNameSan = string.Join(" ", GetPlayerTitle(player),
                    player.displayName.Replace('<', '[').Replace('>', ']'));

                for (int i = 0; i < playersTeam.members.Count; i++)
                {
                    BasePlayer.FindByID(playersTeam.members[i])?
                        .SendConsoleCommand(
                            "chat.add",
                            new object[] {
                                player.userID,
                                string.Format("<color=#B9FC3E>{0}</color>: {1}", playerNameSan, joinedMessage)
                            }
                        );
                };
            }
        }
        #endregion Chat Commands

        #region Discord Verify
        void Discord_Ready(Ready ready)
        {
            Puts("Discord Connected");
        }

        void Discord_MessageCreate(Message message)
        {
            if (message.content.StartsWith(Config["Discord Verification Command"].ToString()))
            {
                Channel channel = discordClient.DiscordServer.channels.Where(x => x.id == message.channel_id).FirstOrDefault();
                if (channel == null)
                {
                    return;
                }

                var splits = message.content.Replace(Config["Discord Verification Command"].ToString(), "").Trim();
                int pin;
                if (!int.TryParse(splits, out pin))
                {
                    channel.CreateMessage(discordClient, $"Error: Cannot parse pin code, try again");
                    return;
                }
                if (pinVerification.ContainsValue(pin))
                {
                    ulong userID = pinVerification.FirstOrDefault(x => x.Value == pin).Key;

                    // Sync SteamID to UserID
                    if (discordUsers.ContainsKey(userID))
                        discordUsers[userID] = message.author.id;
                    else
                        discordUsers.Add(userID, message.author.id);

                    SaveDiscordUsers();

                    discordClient.DiscordServer.GetGuildMember(discordClient, message.author.id, (user) =>
                    {
                        UpdatePlayerClan(user, userID, channel);
                    });

                }
                else
                {
                    channel.CreateMessage(discordClient, $"Error: Pin code not found");
                }
            }
        }

        void ChatCommand_DiscordVerify(BasePlayer player, string command, string[] args)
        {
            if (!(bool)Config["Enable"])
                return;

            if (discordClient == null)
            {
                player.ChatMessage("Discord is not ready... try again later");
                return;
            }

            if (discordUsers.ContainsKey(player.userID))
            {
                player.ChatMessage("You are already verified on Discord...");
                return;
            }

            System.Random random = new System.Random();
            int randomNum = random.Next(1000, 9999);
            while (pinVerification.ContainsValue(randomNum))
            {
                if (pinVerification.Count >= 8999)
                {
                    Puts("Error: No Available Pin Numbers to Allocate");
                    player.ChatMessage("Error: Unable to allocate a PIN");
                }

                randomNum = random.Next(1000, 9999);
            }

            if (pinVerification.ContainsKey(player.userID))
            {
                pinVerification[player.userID] = randomNum;
            }
            else
            {
                pinVerification.Add(player.userID, randomNum);
            }

            player.ChatMessage($"Type <color=#FF0000>{Config["Discord Verification Command"]} {randomNum}</color> in the <color=#FF0000>#verify</color> channel in The Chad Villa discord server");
        }

        void ChatCommand_UpdateTeam(BasePlayer player, string command, string[] args)
        {
            string discordUserID;
            if (!discordUsers.TryGetValue(player.userID, out discordUserID))
            {
                player.ChatMessage($"You must first verify your discord account by typing <color=#FF0000>/{Config["Rust Verification Chat Command"]}</color>");
                return;
            }

            discordClient.DiscordServer.GetGuildMember(discordClient, discordUserID, (discordUser) =>
            {
                UpdatePlayerClan(discordUser, player.userID, null);
                player.ChatMessage("You clan has been updated");
            });
            player.ChatMessage("Server is querying discord for updated roles, please wait.");
        }
        #endregion Discord Verify

        #region Clan Syncing
        void UpdatePlayerClan(GuildMember discordMember, ulong steamId, Channel channel)
        {
            pinVerification.Remove(steamId);

            IEnumerable<Clan> currentClans = Clan.AllClans.Where(x => x.Members.Contains(steamId));

            for (int i = 0; i < currentClans.Count(); i++)
            {
                currentClans.ElementAt(i)?.RemoveMember(steamId);
            }

            Clan newClan = Clan.AllClans.FirstOrDefault(x => discordMember.roles.Any(c => c.Equals(x.DiscordRoleID)));
            // No Appropriate Clan Found.
            if (newClan == null)
            {
                channel?.CreateMessage(discordClient, $"You are not in any clans. Please choose your clan in #choose-your-clan before re-verifying.");
                return;
            }

            newClan.AddMember(steamId);
            channel?.CreateMessage(discordClient, $"Synced Discord clan with server clan.");
        }

        #endregion Clan Syncing

        void LoadDiscordUsers()
        {
            discordUsers = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, string>>("TheVilla_SteamToDiscordMapping");
        }

        void SaveDiscordUsers()
        {
            Interface.Oxide.DataFileSystem.WriteObject("TheVilla_SteamToDiscordMapping", discordUsers);
        }

        public class Clan
        {
            public static List<Clan> AllClans { get; private set; } = new List<Clan>();
            public static string DirectoryName;

            #region Instance Fields
            private string fileName;
            private string clanTag;
            private string discordRoleID;
            private string clanColor;
            #endregion

            #region Properties
            public string DiscordRoleID { get { return discordRoleID; } set { discordRoleID = value; Save(); } }
            public string ClanTag { get { return clanTag; } set { clanTag = value; Save(); } }
            public string ClanColor { get { return clanColor; } set { clanColor = value; Save(); } }

            public List<ulong> Members { get; private set; } = new List<ulong>();
            public RelationshipManager.PlayerTeam TeamUI { get; private set; } = RelationshipManager.Instance.CreateTeam();
            #endregion

            //public List<StabilityEntity> TeamCupboards { get; set; } //todo
            //public Dictionary<string, Vector3> spawns; //todo

            private Clan() { }

            public void AddMember(ulong steamID)
            {
                if (Members.Contains(steamID))
                    return;

                Members.Add(steamID);
                Save();
                AddMemberToTeamUI(BasePlayer.FindByID(steamID));
            }

            public void RemoveMember(ulong steamID)
            {
                if (!Members.Contains(steamID))
                    return;

                Members.Remove(steamID);
                Save();
            }

            public void AddMemberToTeamUI(BasePlayer player)
            {
                if (player == null) return;

                Instance.PrintError("adding player "+player.UserIDString+" to teamUI");
                if (!Members.Contains(player.userID))
                    return;

                if (player.currentTeam != TeamUI.teamID)
                    RelationshipManager.Instance.FindTeam(player.currentTeam)?.RemovePlayer(player.userID);

                TeamUI.AddPlayer(player);
            }

            public void Save()
            {
                if (fileName == null)
                {
                    Instance.PrintError("Filename was empty for Clan: " + clanTag);
                    return;
                }

                ClanConfiguration configuration = new ClanConfiguration
                {
                    DiscordRoleID = discordRoleID,
                    ClanTag = clanTag,
                    ClanColor = clanColor,
                    Members = Members
                };

                Interface.Oxide.DataFileSystem.WriteObject(fileName, configuration);
                Instance.Puts($"Saved Clan Configuration: {fileName}");
            }

            #region Initialization
            public static void LoadClans(string directoryName)
            {
                DirectoryName = directoryName;
                Instance.PrintError(directoryName);
                try
                {
                    string[] configFiles = new string[0];
                    try
                    {
                        configFiles = Interface.Oxide.DataFileSystem.GetFiles(directoryName, "*.json");
                    }
                    catch (Exception ex)
                    {
                        if (ex.GetType().Name != "DirectoryNotFoundException")
                        {
                            Instance.PrintError(ex.ToString());
                            return;
                        }
                    }

                    if (configFiles.Length < 1)
                    {
                        Interface.Oxide.DataFileSystem.WriteObject(DirectoryName + Path.DirectorySeparatorChar + "Template", new ClanConfiguration
                        {
                            DiscordRoleID = "0",
                            ClanTag = "Template Clan Tag",
                            ClanColor = "#0F0F0F",
                            Members = new List<ulong>()
                        });
                        Instance.PrintWarning("No Existing Clan Configurations, Saving Template");
                        return;
                    }

                    for (int i = 0; i < configFiles.Length; i++)
                    {
                        configFiles[i] = configFiles[i].Replace(Interface.Oxide.DataDirectory + "/", "").Replace(".json", "");
                        Instance.PrintError(configFiles[i]);
                        if (configFiles[i].Contains("Template"))
                            continue;
                        Instance.PrintWarning(configFiles[i]);
                        ClanConfiguration clanConfiguration = Interface.Oxide.DataFileSystem.ReadObject<ClanConfiguration>(configFiles[i]);
                        try
                        {
                            AllClans.Add(CreateClan(clanConfiguration, configFiles[i]));
                        }
                        catch (Exception ex)
                        {
                            Instance.PrintError(ex.ToString());
                        }
                    }
                    Instance.Puts($"Loaded {AllClans.Count} Clan[s]");
                }
                catch (Exception ex)
                {
                    Instance.PrintError("FATAL ERROR WHILST LOADING CLANS" + Environment.NewLine + ex.ToString());
                }
            }

            public static Clan CreateClan(ClanConfiguration config, string fileName)
            {
                return new Clan
                {
                    fileName = fileName,
                    discordRoleID = config.DiscordRoleID,
                    clanTag = config.ClanTag,
                    clanColor = config.ClanColor,
                    Members = config.Members ?? new List<ulong>()
                };
            }

            public struct ClanConfiguration
            {
                public string DiscordRoleID { get; set; }
                public string ClanTag { get; set; }
                public string ClanColor { get; set; }
                public List<ulong> Members { get; set; }
            }
            #endregion
        }
    }
}
