﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace FallGuysStats {
    public class LogLine {
        public TimeSpan Time { get; } = TimeSpan.Zero;
        public DateTime Date { get; set; } = DateTime.MinValue;
        public string Line { get; set; }
        public bool IsValid { get; set; }

        public LogLine(string line) {
            Line = line;
            IsValid = line.IndexOf(':') == 2 && line.IndexOf(':', 3) == 5 && line.IndexOf(':', 6) == 12;
            if (IsValid) {
                Time = TimeSpan.Parse(line.Substring(0, 12));
            }
        }

        public int Find(string text) {
            return Line.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString() {
            return $"{Time}: {Line}";
        }
    }

    public class GameState {
        public RoundInfo LastRound { get; set; } = null;
        public List<RoundInfo> CurrentRounds { get; set; } = new List<RoundInfo>();
        public bool CountPlayers { get; set; } = false;
        public bool InParty { get; set; } = false;
        public bool FindPosition { get; set; } = false;
        public string PlayerID { get; set; } = string.Empty;
        public int Ping { get; set; } = 0;
        public int Duration { get; set; } = 0;
    }

    public class LogFileWatcher {
        const int UpdateDelay = 500;

        private string filePath;
        private string prevFilePath;
        private List<LogLine> lines = new List<LogLine>();
        private bool running;
        private bool stop;
        private Thread watcher, parser;

        public event Action<List<RoundInfo>> OnParsedLogLines;
        public event Action<List<RoundInfo>> OnParsedLogLinesCurrent;
        public event Action<DateTime> OnNewLogFileDate;
        public event Action<string> OnError;

        public void Start(string logDirectory, string fileName) {
            if (running) { return; }

            filePath = Path.Combine(logDirectory, fileName);
            prevFilePath = Path.Combine(logDirectory, Path.GetFileNameWithoutExtension(fileName) + "-prev.log");
            stop = false;
            watcher = new Thread(ReadLogFile) { IsBackground = true };
            watcher.Start();
            parser = new Thread(ParseLines) { IsBackground = true };
            parser.Start();
        }

        public async Task Stop() {
            stop = true;
            while (running || watcher == null || watcher.ThreadState == ThreadState.Unstarted) {
                await Task.Delay(50);
            }
            lines = new List<LogLine>();
            await Task.Factory.StartNew(() => watcher?.Join());
            await Task.Factory.StartNew(() => parser?.Join());
        }

        private void ReadLogFile() {
            running = true;
            List<LogLine> tempLines = new List<LogLine>();
            bool completed = false;
            DateTime currentDate = DateTime.MinValue;
            string currentFilePath = prevFilePath;
            long offset = 0;
            while (!stop) {
                try {
                    if (File.Exists(currentFilePath)) {
                        using (FileStream fs = new FileStream(currentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                            tempLines.Clear();

                            // Check if the size of the file changed
                            if (fs.Length > offset) {
                                fs.Seek(offset, SeekOrigin.Begin);

                                using (StreamReader sr = new StreamReader(fs)) {
                                    string line;
                                    while (!sr.EndOfStream && (line = sr.ReadLine()) != null) {
                                        LogLine logLine = new LogLine(line);
                                        if (logLine.IsValid) {
                                            int index;

                                            // Start of the current session
                                            if ((index = logLine.Find("[GlobalGameStateClient].PreStart called at ")) > 0) {
                                                currentDate = DateTime.SpecifyKind(DateTime.Parse(line.Substring(index + 43, 19)), DateTimeKind.Utc);
                                                OnNewLogFileDate?.Invoke(currentDate);
                                            }

                                            // Set current date to the logline
                                            if (currentDate != DateTime.MinValue) {
                                                if ((int)currentDate.TimeOfDay.TotalSeconds > (int)logLine.Time.TotalSeconds) {
                                                    currentDate = currentDate.AddDays(1);
                                                }
                                                currentDate = currentDate.AddSeconds(logLine.Time.TotalSeconds - currentDate.TimeOfDay.TotalSeconds);
                                                logLine.Date = currentDate;
                                            }

                                            // The lines below the current line is the info you get when a show ends so we add htose lines too.
                                            if (logLine.Find(" == [CompletedEpisodeDto] ==") > 0) {
                                                StringBuilder sb = new StringBuilder(line);
                                                sb.AppendLine();
                                                LogLine temp;
                                                while ((line = sr.ReadLine()) != null) {
                                                    temp = new LogLine(line);
                                                    if (temp.IsValid) {
                                                        break;
                                                    } else if (!string.IsNullOrEmpty(line)) {
                                                        sb.AppendLine(line);
                                                    }
                                                logLine.Line = sb.ToString();
                                                tempLines.Add(logLine);
                                                tempLines.Add(temp);
                                                }
                                            } else {
                                                tempLines.Add(logLine);
                                            }

                                            offset = fs.Position;

                                         // The line we use to get the ping is not Valid but we add it anyways
                                        } else if (logLine.Find("Client address: ") > 0) {
                                            tempLines.Add(logLine);
                                        }

                                        
                                    }
                                }
                            
                            // The logfile was recreated?
                            } else if (offset > fs.Length) {
                                offset = 0;
                            }
                        }
                    }

                    // After reading Player-prev.log switch to Player.log
                    if (!completed) {
                        completed = true;
                        offset = 0;
                        currentFilePath = filePath;
                    }

                    if (tempLines.Count > 0) {
                        lock (lines) {
                            lines.AddRange(tempLines);
                            tempLines.Clear();
                        }
                    }
                } catch (Exception ex) {
                    OnError?.Invoke(ex.ToString());
                }
                Thread.Sleep(UpdateDelay);
            }
            running = false;
        }

        private void ParseLines() {
            List<RoundInfo> allStats = new List<RoundInfo>();
            GameState gameState = new GameState();
            
            while (!stop) {
                try {
                    lock (lines) {
                        for (int i = 0; i < lines.Count; i++) {
                            LogLine line = lines[i];
                            if (ParseLine(line, ref gameState)) {
                                allStats.AddRange(gameState.CurrentRounds);
                            }
                            lines.Clear();
                        }
                    }
                     
                    // Process all the stats from completed rounds
                    if (allStats.Count > 0) {
                        OnParsedLogLines?.Invoke(allStats);
                        allStats.Clear();
                    }

                    OnParsedLogLinesCurrent?.Invoke(gameState.CurrentRounds);

                    if (gameState.Ping != 0) {
                        Stats.LastServerPing = gameState.Ping;
                    }

                } catch (Exception ex) {
                    OnError?.Invoke(ex.ToString());
                }
                Thread.Sleep(UpdateDelay);
            }
        }
 
        private bool ParseLine(LogLine line, ref GameState state) {
            int index;
            if ((index = line.Find("[StateGameLoading] Finished loading game level")) > 0) {
                RoundInfo round = new RoundInfo();
                round.Name = line.Line.Substring(index + 62);
                if ((index = round.Name.IndexOf("_event_only", StringComparison.OrdinalIgnoreCase)) > 0) {
                    round.Name = round.Name.Substring(0, index);
                }
                
                round.Round = state.CurrentRounds.Count + 1;
                round.Start = line.Date;
                round.InParty = state.InParty;
                round.GameDuration = state.Duration;
                state.CountPlayers = true;

                state.LastRound = round;
                state.CurrentRounds.Add(round);
            } else if ((index = line.Find("[StateMatchmaking] Begin matchmaking"e)) > 0) {
                state.InParty = !line.Line.Substring(index + 37).Equals("solo", StringComparison.OrdinalIgnoreCase);
                if (state.CurrentRounds != null) {
                    if (state.LastRound.End == DateTime.MinValue) {
                        state.LastRound.End = line.Date;
                    }
                    state.LastRound.Playing = false;
                }
                Stats.InShow = true;
                state.CurrentRounds.Clear();
                state.LastRound = null;
            } else if ((index = line.Find("NetworkGameOptions: durationInSeconds=")) > 0) {
                int nextIndex = line.Line.IndexOf(" ", index + 38);
                state.Duration = int.Parse(line.Line.Substring(index + 38, nextIndex - index - 38));
            } else if (state.LastRound != null && state.CountPlayers && line.Find("[ClientGameManager] Added player ") > 0 && (index = line.Find(" players in system.")) > 0) {
                int prevIndex = line.Line.LastIndexOf(' ', index - 1);
                if (int.TryParse(line.Line.Substring(prevIndex, index - prevIndex), out int players)) {
                    state.LastRound.Players = players;
                }
            } else if ((index = line.Find("[ClientGameManager] Handling bootstrap for local player FallGuy [")) > 0) {
                int prevIndex = line.Line.IndexOf(']', index + 65);
                state.PlayerID = line.Line.Substring(index + 65, prevIndex - index - 65);
            } else if (state.LastRound != null && line.Find($"[ClientGameManager] Handling unspawn for player FallGuy [{state.PlayerID}]") > 0) {
                if (state.LastRound.End == DateTime.MinValue) {
                    state.LastRound.Finish = line.Date;
                } else {
                    state.LastRound.Finish = state.LastRound.End;
                }
                state.FindPosition = true;
            } else if (state.LastRound != null && state.FindPosition && (index = line.Find("[ClientGameSession] NumPlayersAchievingObjective=")) > 0) {
                int position = int.Parse(line.Line.Substring(index + 49));
                if (position > 0) {
                    state.FindPosition = false;
                    state.LastRound.Position = position;
                }
            } else if (state.LastRound != null && line.Find("Client address: ") > 0) {
                index = line.Find("RTT: ");
                if (index > 0) {
                    int msIndex = line.Line.IndexOf("ms", index);
                    state.Ping = int.Parse(line.Line.Substring(index + 5, msIndex - index - 5));
                }
            } else if (state.LastRound != null && line.Find("[GameSession] Changing state from Countdown to Playing") > 0) {
                state.LastRound.Start = line.Date;
                state.LastRound.Playing = true;
                state.CountPlayers = false;
            } else if (state.LastRound != null &&
                (line.Find("[GameSession] Changing state from Playing to GameOver") > 0
                || line.Find("Changing local player state to: SpectatingEliminated") > 0
                || line.Find("[GlobalGameStateClient] SwitchToDisconnectingState") > 0)) {
                if (state.LastRound.End == DateTime.MinValue) {
                    state.LastRound.End = line.Date;
                }
                state.LastRound.Playing = false;
                state.FindPosition = false;
            } else if (line.Find("[StateMainMenu] Loading scene MainMenu") > 0) {
                if (state.LastRound != null) {
                    if (state.LastRound.End == DateTime.MinValue) {
                        state.LastRound.End = line.Date;
                    }
                    state.LastRound.Playing = false;
                }
                state.FindPosition = false;
                state.CountPlayers = false;
                Stats.InShow = false;
            } else if (line.Find(" == [CompletedEpisodeDto] ==") > 0) {
                if (state.LastRound == null) { return false; }

                RoundInfo temp = null;
                StringReader sr = new StringReader(line.Line);
                string detail;
                bool foundRound = false;
                int maxRound = 0;
                DateTime showStart = DateTime.MinValue;
                while ((detail = sr.ReadLine()) != null) {
                    if (detail.IndexOf("[Round ", StringComparison.OrdinalIgnoreCase) == 0) {
                        foundRound = true;
                        int roundNum = (int)detail[7] - 0x30 + 1;
                        string roundName = detail.Substring(11, detail.Length - 12);
                        if ((index = roundName.IndexOf("_event_only", StringComparison.OrdinalIgnoreCase)) > 0) {
                            roundName = roundName.Substring(0, index);
                        }

                        if (roundNum - 1 < state.CurrentRounds.Count) {
                            if (roundNum > maxRound) {
                                maxRound = roundNum;
                            }

                            temp = state.CurrentRounds[roundNum - 1];
                            if (!temp.Name.Equals(roundName, StringComparison.OrdinalIgnoreCase)) {
                                return false;
                            }

                            if (roundNum == 1) {
                                showStart = temp.Start;
                            }
                            temp.ShowStart = showStart;
                            temp.Playing = false;
                            temp.Round = roundNum;
                            state.InParty = temp.InParty;
                        } else {
                            return false;
                        }

                        if (temp.End == DateTime.MinValue) {
                            temp.End = line.Date;
                        }
                        if (temp.Start == DateTime.MinValue) {
                            temp.Start = temp.End;
                        }
                        if (!temp.Finish.HasValue) {
                            temp.Finish = temp.End;
                        }
                    } else if (foundRound) {
                        if (detail.IndexOf("> Position: ", StringComparison.OrdinalIgnoreCase) == 0) {
                            temp.Position = int.Parse(detail.Substring(12));
                        } else if (detail.IndexOf("> Team Score: ", StringComparison.OrdinalIgnoreCase) == 0) {
                            temp.Score = int.Parse(detail.Substring(14));
                        } else if (detail.IndexOf("> Qualified: ", StringComparison.OrdinalIgnoreCase) == 0) {
                            char qualified = detail[13];
                            temp.Qualified = qualified == 'T';
                            temp.Finish = temp.Qualified ? temp.Finish : null;
                        } else if (detail.IndexOf("> Bonus Tier: ", StringComparison.OrdinalIgnoreCase) == 0 && detail.Length == 15) {
                            char tier = detail[14];
                            temp.Tier = (int)tier - 0x30 + 1;
                        } else if (detail.IndexOf("> Kudos: ", StringComparison.OrdinalIgnoreCase) == 0) {
                            temp.Kudos += int.Parse(detail.Substring(9));
                        } else if (detail.IndexOf("> Bonus Kudos: ", StringComparison.OrdinalIgnoreCase) == 0) {
                            temp.Kudos += int.Parse(detail.Substring(15));
                        }
                    }
                }

                if (state.CurrentRounds.Count > maxRound) {
                    return false;
                }

                state.LastRound = state.CurrentRounds[state.CurrentRounds.Count - 1];
                DateTime showEnd = state.LastRound.End;
                for (int i = 0; i < state.CurrentRounds.Count; i++) {
                    state.CurrentRounds[i].ShowEnd = showEnd;
                }
                if (state.LastRound.Qualified) {
                    state.LastRound.Crown = true;
                }
                state.LastRound = null;
                Stats.InShow = false;
                Stats.EndedShow = true;
                return true;
            }
            return false;
        }
    }
}