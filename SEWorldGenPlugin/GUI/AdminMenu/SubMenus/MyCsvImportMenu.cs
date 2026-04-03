using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SEWorldGenPlugin.GUI.Controls;
using SEWorldGenPlugin.Session;
using SEWorldGenPlugin.Utilities;
using System.Text;
using VRageMath;

namespace SEWorldGenPlugin.GUI.AdminMenu.SubMenus
{
    /// <summary>
    /// Admin sub menu that allows importing planets, moons, and asteroid belts/rings
    /// from a CSV file into the star system.
    /// </summary>
    public class MyCsvImportMenu : MyPluginAdminMenuSubMenu
    {
        private MyGuiControlTextbox m_filePathBox;
        private MyGuiControlButton m_validateButton;
        private MyGuiControlButton m_importButton;
        private MyGuiControlLabel m_statusLabel;
        private MyCsvImporter m_importer;
        private bool m_isImporting;

        public override string GetTitle()
        {
            return "CSV Import";
        }

        public override bool IsVisible()
        {
            return MyPluginSession.Static.ServerVersionMatch &&
                (MySession.Static.IsUserAdmin(Sync.MyId) ||
                MySession.Static.IsUserSpaceMaster(Sync.MyId)) &&
                MySettingsSession.Static.Settings.Enabled;
        }

        public override void RefreshInternals(MyGuiControlParentTableLayout parent, float maxWidth, MyAdminMenuExtension instance)
        {
            parent.AddTableRow(new MyGuiControlLabel(text: "CSV File Path"));

            m_filePathBox = new MyGuiControlTextbox();
            m_filePathBox.Size = new Vector2(maxWidth, m_filePathBox.Size.Y);
            m_filePathBox.SetToolTip("Full path to the CSV file to import (e.g. C:\\MySystem.csv)");
            parent.AddTableRow(m_filePathBox);

            parent.AddTableSeparator();

            m_validateButton = MyPluginGuiHelper.CreateDebugButton(maxWidth, "Validate CSV", delegate (MyGuiControlButton button)
            {
                OnValidate();
            });
            parent.AddTableRow(m_validateButton);

            m_importButton = MyPluginGuiHelper.CreateDebugButton(maxWidth, "Import CSV", delegate (MyGuiControlButton button)
            {
                OnImport();
            });
            parent.AddTableRow(m_importButton);

            parent.AddTableSeparator();

            m_statusLabel = new MyGuiControlLabel(text: "Ready");
            parent.AddTableRow(m_statusLabel);
        }

        private void OnValidate()
        {
            string filePath = GetFilePath();
            if (filePath == null) return;

            var importer = new MyCsvImporter();
            System.Collections.Generic.List<string> errors;
            bool valid = importer.ValidateFile(filePath, out errors);

            if (valid)
            {
                MyPluginGuiHelper.DisplayMessage("CSV file is valid and ready to import.", "Validation Success");
            }
            else
            {
                string errorMsg = string.Join("\n", errors);
                if (errorMsg.Length > 1000)
                    errorMsg = errorMsg.Substring(0, 1000) + "\n...";
                MyPluginGuiHelper.DisplayError(errorMsg, "Validation Failed");
            }
        }

        private void OnImport()
        {
            if (m_isImporting) return;

            string filePath = GetFilePath();
            if (filePath == null) return;

            m_importer = new MyCsvImporter();
            System.Collections.Generic.List<string> errors;
            bool valid = m_importer.ValidateFile(filePath, out errors);

            if (!valid)
            {
                string errorMsg = string.Join("\n", errors);
                if (errorMsg.Length > 1000)
                    errorMsg = errorMsg.Substring(0, 1000) + "\n...";
                MyPluginGuiHelper.DisplayError(errorMsg, "Validation Failed");
                m_importer = null;
                return;
            }

            m_isImporting = true;
            m_validateButton.Enabled = false;
            m_importButton.Enabled = false;

            m_importer.StartImport(
                progress =>
                {
                    m_statusLabel.Text = progress;
                },
                (success, message) =>
                {
                    m_isImporting = false;
                    m_validateButton.Enabled = true;
                    m_importButton.Enabled = true;
                    m_statusLabel.Text = success ? "Complete" : "Finished with errors";
                    m_importer = null;

                    if (success)
                        MyPluginGuiHelper.DisplayMessage(message, "Import Complete");
                    else
                        MyPluginGuiHelper.DisplayError(message, "Import Result");
                }
            );
        }

        private string GetFilePath()
        {
            StringBuilder sb = new StringBuilder();
            m_filePathBox.GetText(sb);
            string filePath = sb.ToString().Trim();

            if (string.IsNullOrEmpty(filePath))
            {
                MyPluginGuiHelper.DisplayError("Please enter a file path.", "Error");
                return null;
            }

            return filePath;
        }

        public override void Close()
        {
            if (m_importer != null)
            {
                m_importer.Cancel();
                m_importer = null;
            }
            m_isImporting = false;
        }

        public override void Draw()
        {
        }

        public override void HandleInput()
        {
        }
    }
}
