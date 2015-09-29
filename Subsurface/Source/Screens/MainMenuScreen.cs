﻿using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Subsurface.Networking;
using System.IO;
using System.Xml.Linq;

namespace Subsurface
{
    class MainMenuScreen : Screen
    {
        public enum Tabs { NewGame = 1, LoadGame = 2, HostServer = 3 }

        GUIFrame buttonsTab;

        private GUIFrame[] menuTabs;
        private GUIListBox mapList;

        private GUIListBox saveList;

        private GUITextBox saveNameBox, seedBox;

        private GUITextBox serverNameBox, portBox, passwordBox, maxPlayersBox;
        private GUITickBox isPublicBox, useUpnpBox;

        private GameMain game;

        int selectedTab;

        public MainMenuScreen(GameMain game)
        {
            menuTabs = new GUIFrame[Enum.GetValues(typeof(Tabs)).Length+1];



            buttonsTab = new GUIFrame(new Rectangle(50, 200, 200, 500), Color.Transparent, Alignment.Left);
            //menuTabs[(int)Tabs.Main].Padding = GUI.style.smallPadding;

            Rectangle panelRect = new Rectangle(
                GameMain.GraphicsWidth / 2 - 250,
                buttonsTab.Rect.Y,
                500, 360);

            GUIButton button = new GUIButton(new Rectangle(0, 0, 0, 30), "Tutorial", Alignment.CenterX, GUI.Style, buttonsTab);
            button.OnClicked = TutorialButtonClicked;

            button = new GUIButton(new Rectangle(0, 70, 0, 30), "New Game", Alignment.CenterX, GUI.Style, buttonsTab);
            button.UserData = (int)Tabs.NewGame;
            button.OnClicked = SelectTab;

            button = new GUIButton(new Rectangle(0, 130, 0, 30), "Load Game", Alignment.CenterX, GUI.Style, buttonsTab);
            button.UserData = (int)Tabs.LoadGame;
            button.OnClicked = SelectTab;

            button = new GUIButton(new Rectangle(0, 200, 0, 30), "Join Server", Alignment.CenterX, GUI.Style, buttonsTab);
            //button.UserData = (int)Tabs.JoinServer;
            button.OnClicked = JoinServerClicked;

            button = new GUIButton(new Rectangle(0, 260, 0, 30), "Host Server", Alignment.CenterX, GUI.Style, buttonsTab);
            button.UserData = (int)Tabs.HostServer;
            button.OnClicked = SelectTab;

            button = new GUIButton(new Rectangle(0, 330, 0, 30), "Quit", Alignment.CenterX, GUI.Style, buttonsTab);
            button.OnClicked = QuitClicked;

            //----------------------------------------------------------------------

            menuTabs[(int)Tabs.NewGame] = new GUIFrame(panelRect, GUI.Style);
            //menuTabs[(int)Tabs.NewGame].Padding = GUI.style.smallPadding;

            //new GUITextBlock(new Rectangle(0, -20, 0, 30), "New Game", null, null, Alignment.CenterX, GUI.style, menuTabs[(int)Tabs.NewGame]);

            new GUITextBlock(new Rectangle(0, 0, 0, 30), "Selected submarine:", null, null, Alignment.Left, GUI.Style, menuTabs[(int)Tabs.NewGame]);
            mapList = new GUIListBox(new Rectangle(0, 30, 200, panelRect.Height-100), GUI.Style, menuTabs[(int)Tabs.NewGame]);

            foreach (Submarine sub in Submarine.SavedSubmarines)
            {
                GUITextBlock textBlock = new GUITextBlock(
                    new Rectangle(0, 0, 0, 25),
                    sub.Name, 
                    GUI.Style,
                    Alignment.Left, Alignment.Left, mapList);
                textBlock.Padding = new Vector4(10.0f, 0.0f, 0.0f, 0.0f);
                textBlock.UserData = sub;
            }
            if (Submarine.SavedSubmarines.Count > 0) mapList.Select(Submarine.SavedSubmarines[0]);

            new GUITextBlock(new Rectangle((int)(mapList.Rect.Width + 20), 0, 100, 20),
                "Save name: ", GUI.Style, Alignment.Left, Alignment.Left, menuTabs[(int)Tabs.NewGame]);

            saveNameBox = new GUITextBox(new Rectangle((int)(mapList.Rect.Width + 20), 30, 180, 20),
                Alignment.TopLeft, GUI.Style, menuTabs[(int)Tabs.NewGame]);
            saveNameBox.Text = SaveUtil.CreateSavePath();

            new GUITextBlock(new Rectangle((int)(mapList.Rect.Width + 20), 60, 100, 20),
                "Map Seed: ", GUI.Style, Alignment.Left, Alignment.Left, menuTabs[(int)Tabs.NewGame]);

            seedBox = new GUITextBox(new Rectangle((int)(mapList.Rect.Width + 20), 90, 180, 20),
                Alignment.TopLeft, GUI.Style, menuTabs[(int)Tabs.NewGame]);
            seedBox.Text = ToolBox.RandomSeed(8);


            button = new GUIButton(new Rectangle(0, 0, 100, 30), "Start", Alignment.BottomRight, GUI.Style,  menuTabs[(int)Tabs.NewGame]);
            button.OnClicked = StartGame;

            //----------------------------------------------------------------------

            menuTabs[(int)Tabs.LoadGame] = new GUIFrame(panelRect, GUI.Style);
            //menuTabs[(int)Tabs.LoadGame].Padding = GUI.style.smallPadding;


            menuTabs[(int)Tabs.HostServer] = new GUIFrame(panelRect, GUI.Style);
            //menuTabs[(int)Tabs.JoinServer].Padding = GUI.style.smallPadding;

            //new GUITextBlock(new Rectangle(0, -25, 0, 30), "Host Server", GUI.style, Alignment.CenterX, Alignment.CenterX, menuTabs[(int)Tabs.HostServer], false, GUI.LargeFont);

            new GUITextBlock(new Rectangle(0, 0, 0, 30), "Server Name:", GUI.Style, Alignment.TopLeft, Alignment.Left, menuTabs[(int)Tabs.HostServer]);
            serverNameBox = new GUITextBox(new Rectangle(160, 0, 200, 30), null, null, Alignment.TopLeft, Alignment.Left, GUI.Style, menuTabs[(int)Tabs.HostServer]);

            new GUITextBlock(new Rectangle(0, 50, 0, 30), "Server port:", GUI.Style, Alignment.TopLeft, Alignment.Left, menuTabs[(int)Tabs.HostServer]);
            portBox = new GUITextBox(new Rectangle(160, 50, 200, 30), null, null, Alignment.TopLeft, Alignment.Left, GUI.Style, menuTabs[(int)Tabs.HostServer]);
            portBox.Text = NetConfig.DefaultPort.ToString();
            portBox.ToolTip = "Server port";

            new GUITextBlock(new Rectangle(0, 100, 100, 30), "Max players:", GUI.Style, Alignment.TopLeft, Alignment.Left, menuTabs[(int)Tabs.HostServer]);
            maxPlayersBox = new GUITextBox(new Rectangle(195, 100, 30, 30), null, null, Alignment.TopLeft, Alignment.Center, GUI.Style, menuTabs[(int)Tabs.HostServer]);
            maxPlayersBox.Text = "8";
            maxPlayersBox.Enabled = false;

            var plusPlayersBox = new GUIButton(new Rectangle(230, 100, 30, 30), "+", GUI.Style, menuTabs[(int)Tabs.HostServer]);
            plusPlayersBox.UserData = 1;
            plusPlayersBox.OnClicked = ChangeMaxPlayers;

            var minusPlayersBox = new GUIButton(new Rectangle(160, 100, 30, 30), "-", GUI.Style, menuTabs[(int)Tabs.HostServer]);
            minusPlayersBox.UserData = -1;
            minusPlayersBox.OnClicked = ChangeMaxPlayers;

            new GUITextBlock(new Rectangle(0, 150, 0, 30), "Password (optional):", GUI.Style, Alignment.TopLeft, Alignment.Left, menuTabs[(int)Tabs.HostServer]);
            passwordBox = new GUITextBox(new Rectangle(160, 150, 200, 30), null, null, Alignment.TopLeft, Alignment.Left, GUI.Style, menuTabs[(int)Tabs.HostServer]);
            
            isPublicBox = new GUITickBox(new Rectangle(10, 200, 20, 20), "Public server", Alignment.TopLeft, menuTabs[(int)Tabs.HostServer]);
            isPublicBox.ToolTip = "Public servers are shown in the list of available servers in the ''Join Server'' -tab";


            useUpnpBox = new GUITickBox(new Rectangle(10, 250, 20, 20), "Attempt UPnP port forwarding", Alignment.TopLeft, menuTabs[(int)Tabs.HostServer]);
            useUpnpBox.ToolTip = "UPnP can be used for forwarding ports on your router to allow players join the server."
            + " However, UPnP isn't supported by all routers, so you may need to setup port forwards manually"
            +" if players are unable to join the server (see the readme for instructions).";
            
            GUIButton hostButton = new GUIButton(new Rectangle(0, 0, 100, 30), "Start", Alignment.BottomRight, GUI.Style, menuTabs[(int)Tabs.HostServer]);
            hostButton.OnClicked = HostServerClicked;

            this.game = game;
        }

        public override void Select()
        {
            base.Select();

            SelectTab(null, 0);
            //selectedTab = 0;
        }
        
        public bool SelectTab(GUIButton button, object obj)
        {
            selectedTab = (int)obj;

            if (button != null) button.Selected = true;
            
            foreach (GUIComponent child in buttonsTab.children)
            {
                GUIButton otherButton = child as GUIButton;
                if (otherButton == null || otherButton == button) continue;

                otherButton.Selected = false;
            }

            if (selectedTab == (int)Tabs.LoadGame) UpdateLoadScreen();

            if (Selected != this) this.Select();
            return true;
        }

        private bool TutorialButtonClicked(GUIButton button, object obj)
        {
            TutorialMode.Start();

            return true;
        }

        private bool JoinServerClicked(GUIButton button, object obj)
        {            
            GameMain.ServerListScreen.Select();
            return true;
        }

        private bool ChangeMaxPlayers(GUIButton button, object obj)
        {
            int currMaxPlayers = 10;

            int.TryParse(maxPlayersBox.Text, out currMaxPlayers);
            currMaxPlayers = (int)MathHelper.Clamp(currMaxPlayers+(int)button.UserData, 1, 10);

            maxPlayersBox.Text = currMaxPlayers.ToString();

            return true;
        }

        private bool HostServerClicked(GUIButton button, object obj)
        {
            string name = serverNameBox.Text;
            if (string.IsNullOrEmpty(name))
            {
                serverNameBox.Flash();
                return false;
            }

            int port;
            if (!int.TryParse(portBox.Text, out port) || port < 0 || port > 65535)
            {
                portBox.Text = NetConfig.DefaultPort.ToString();
                portBox.Flash();

                return false;
            }

            GameMain.NetworkMember = new GameServer(name, port, isPublicBox.Selected, passwordBox.Text, useUpnpBox.Selected, int.Parse(maxPlayersBox.Text));
            
            GameMain.NetLobbyScreen.IsServer = true;
            //Game1.NetLobbyScreen.Select();
            return true;
        }

        private bool QuitClicked(GUIButton button, object obj)
        {
            game.Exit();
            return true;
        }

        private void UpdateLoadScreen()
        {
            menuTabs[(int)Tabs.LoadGame].ClearChildren();

            string[] saveFiles = SaveUtil.GetSaveFiles();

            saveList = new GUIListBox(new Rectangle(0, 0, 200, menuTabs[(int)Tabs.LoadGame].Rect.Height - 80), Color.White, GUI.Style, menuTabs[(int)Tabs.LoadGame]);
            saveList.OnSelected = SelectSaveFile;

            foreach (string saveFile in saveFiles)
            {
                GUITextBlock textBlock = new GUITextBlock(
                    new Rectangle(0, 0, 0, 25),
                    saveFile,
                    GUI.Style,
                    Alignment.Left,
                    Alignment.Left,
                    saveList);
                textBlock.Padding = new Vector4(10.0f, 0.0f, 0.0f, 0.0f);
                textBlock.UserData = saveFile;
            }

            var button = new GUIButton(new Rectangle(0, 0, 100, 30), "Start", Alignment.Right | Alignment.Bottom, GUI.Style, menuTabs[(int)Tabs.LoadGame]);
            button.OnClicked = LoadGame;

        }

        private bool SelectSaveFile(GUIComponent component, object obj)
        {
            string fileName = (string)obj;
            
            XDocument doc = SaveUtil.LoadGameSessionDoc(fileName);

            if (doc==null)
            {
                DebugConsole.ThrowError("Error loading save file ''"+fileName+"''. The file may be corrupted.");
                return false;
            }

            RemoveSaveFrame();

            string saveTime = ToolBox.GetAttributeString(doc.Root, "savetime", "unknown");

            XElement modeElement = null;
            foreach (XElement element in doc.Root.Elements())
            {
                if (element.Name.ToString().ToLower() != "gamemode") continue;

                modeElement = element;
                break;
            }

            string mapseed = ToolBox.GetAttributeString(modeElement, "mapseed", "unknown");

            GUIFrame saveFileFrame = new GUIFrame(new Rectangle((int)(saveList.Rect.Width + 20), 0, 200, 200), Color.Black*0.4f, GUI.Style, menuTabs[(int)Tabs.LoadGame]);
            saveFileFrame.UserData = "savefileframe";
            saveFileFrame.Padding = new Vector4(20.0f, 20.0f, 20.0f, 20.0f);

            new GUITextBlock(new Rectangle(0,0,0,20), fileName, GUI.Style, saveFileFrame);

            new GUITextBlock(new Rectangle(0, 30, 0, 20), "Last saved: ", GUI.Style, saveFileFrame).Font = GUI.SmallFont;
            new GUITextBlock(new Rectangle(15, 45, 0, 20), saveTime, GUI.Style, saveFileFrame).Font = GUI.SmallFont;

            new GUITextBlock(new Rectangle(0, 65, 0, 20), "Map seed: ", GUI.Style, saveFileFrame).Font = GUI.SmallFont;
            new GUITextBlock(new Rectangle(15, 80, 0, 20), mapseed, GUI.Style, saveFileFrame).Font = GUI.SmallFont;

            var deleteSaveButton = new GUIButton(new Rectangle(0, 0, 100, 20), "Delete", Alignment.BottomCenter, GUI.Style, saveFileFrame);
            deleteSaveButton.UserData = fileName;
            deleteSaveButton.OnClicked = DeleteSave;

            return true;
        }

        private bool DeleteSave(GUIButton button, object obj)
        {
            string saveFile = obj as string;

            if (obj == null) return false;

            SaveUtil.DeleteSave(saveFile);

            UpdateLoadScreen();

            return true;
        }

        private void RemoveSaveFrame()
		{
            GUIComponent prevFrame = null;
            foreach (GUIComponent child in menuTabs[(int)Tabs.LoadGame].children)
            {
                if (child.UserData as string != "savefileframe") continue;

                prevFrame = child;
                break;
            }
            menuTabs[(int)Tabs.LoadGame].RemoveChild(prevFrame);
        }

        public override void Update(double deltaTime)
        {
            buttonsTab.Update((float)deltaTime);
            if (selectedTab>0) menuTabs[selectedTab].Update((float)deltaTime);

            GameMain.TitleScreen.TitlePosition =
                Vector2.Lerp(GameMain.TitleScreen.TitlePosition, new Vector2(
                    GameMain.TitleScreen.TitleSize.X / 2.0f * GameMain.TitleScreen.Scale + 30.0f,
                    GameMain.TitleScreen.TitleSize.Y / 2.0f * GameMain.TitleScreen.Scale + 30.0f), 
                    0.1f);
                
        }

        public override void Draw(double deltaTime, GraphicsDevice graphics, SpriteBatch spriteBatch)
        {
            graphics.Clear(Color.CornflowerBlue);

            GameMain.TitleScreen.Draw(spriteBatch, graphics, -1.0f, (float)deltaTime);

            //Game1.GameScreen.DrawMap(graphics, spriteBatch);
            
            spriteBatch.Begin();

            buttonsTab.Draw(spriteBatch);
            if (selectedTab>0) menuTabs[selectedTab].Draw(spriteBatch);

            GUI.Draw((float)deltaTime, spriteBatch, null);

            spriteBatch.DrawString(GUI.Font, "Subsurface v"+GameMain.Version, new Vector2(10, GameMain.GraphicsHeight-20), Color.White);

            spriteBatch.End();
        }

        private bool StartGame(GUIButton button, object obj)
        {
            if (string.IsNullOrEmpty(saveNameBox.Text)) return false;

            string[] existingSaveFiles = SaveUtil.GetSaveFiles();

            if (Array.Find(existingSaveFiles, s => s == saveNameBox.Text)!=null)
            {
                new GUIMessageBox("Save name already in use", "Please choose another name for the save file");
                return false;
            }

            Submarine selectedSub = mapList.SelectedData as Submarine;
            if (selectedSub == null) return false;

            GameMain.GameSession = new GameSession(selectedSub, saveNameBox.Text, GameModePreset.list.Find(gm => gm.Name == "Single Player"));
            (GameMain.GameSession.gameMode as SinglePlayerMode).GenerateMap(seedBox.Text);

            GameMain.LobbyScreen.Select();

            new GUIMessageBox("Welcome to Subsurface!", "Please note that the single player mode is very unfinished at the moment; "+
            "for example, the NPCs don't have an AI yet and there are only a couple of different quests to complete. The multiplayer "+
            "mode should be much more enjoyable to play at the moment, so my recommendation is to try out and get a hang of the game mechanics "+
            "in the single player mode and then move on to multiplayer. Have fun!\n - Regalis, the main dev of Subsurface", 400, 350);

            return true;
        }

        private bool PreviousTab(GUIButton button, object obj)
        {
            //selectedTab = (int)Tabs.Main;

            return true;
        }

        private bool LoadGame(GUIButton button, object obj)
        {

            string saveFile = saveList.SelectedData as string;
            if (string.IsNullOrWhiteSpace(saveFile)) return false;

            try
            {
                SaveUtil.LoadGame(saveFile);                
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Loading map ''"+saveFile+"'' failed", e);
                return false;
            }


            GameMain.LobbyScreen.Select();

            return true;
        }

    }
}