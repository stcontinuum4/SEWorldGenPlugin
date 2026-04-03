using SEWorldGenPlugin.Generator;
using SEWorldGenPlugin.Generator.AsteroidObjects;
using SEWorldGenPlugin.Generator.AsteroidObjects.AsteroidRing;
using SEWorldGenPlugin.ObjectBuilders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VRageMath;

namespace SEWorldGenPlugin.Utilities
{
    /// <summary>
    /// Types of objects that can be imported from a CSV file.
    /// </summary>
    public enum CsvRowType
    {
        PLANET,
        MOON,
        RING,
        BELT
    }

    /// <summary>
    /// Represents a single parsed row from a CSV import file.
    /// </summary>
    public class CsvRow
    {
        public CsvRowType Type;
        public string Name;
        public string ParentName;
        public string SubtypeId;
        public double Diameter;
        public Vector3D Position;
        public double RingRadius;
        public double RingWidth;
        public double RingHeight;
        public Vector3D RingAngle;
        public long AsteroidSizeMin;
        public long AsteroidSizeMax;
        public int LineNumber;
    }

    /// <summary>
    /// Handles parsing, validating, and importing star system objects from a CSV file.
    /// Objects are added sequentially via async network callbacks to ensure parents
    /// exist before their children are added.
    /// </summary>
    public class MyCsvImporter
    {
        private List<CsvRow> m_rows;
        private Queue<CsvRow> m_importQueue;
        private Dictionary<string, Guid> m_nameToId;
        private Action<string> m_progressCallback;
        private Action<bool, string> m_completionCallback;
        private int m_totalRows;
        private int m_processedRows;
        private int m_failedRows;
        private List<string> m_failedNames;
        private bool m_cancelled;

        /// <summary>
        /// Validates a CSV file without importing it.
        /// </summary>
        /// <param name="filePath">Path to the CSV file</param>
        /// <param name="errors">List of validation errors found</param>
        /// <returns>True if the file is valid</returns>
        public bool ValidateFile(string filePath, out List<string> errors)
        {
            errors = new List<string>();

            if (!File.Exists(filePath))
            {
                errors.Add("File not found: " + filePath);
                return false;
            }

            List<CsvRow> rows;
            try
            {
                rows = ParseCsv(filePath, errors);
            }
            catch (Exception ex)
            {
                errors.Add("Failed to read file: " + ex.Message);
                return false;
            }

            if (rows == null || rows.Count == 0)
            {
                if (errors.Count == 0)
                    errors.Add("No data rows found in CSV file.");
                return false;
            }

            // Cross-row validation
            HashSet<string> planetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Collect existing system planet names
            HashSet<string> existingPlanetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var starSystem = MyStarSystemGenerator.Static?.StarSystem;
            if (starSystem != null)
            {
                foreach (var obj in starSystem.GetAllByType(MySystemObjectType.PLANET))
                {
                    existingPlanetNames.Add(obj.DisplayName);
                }
            }

            foreach (var row in rows)
            {
                if (!allNames.Add(row.Name))
                {
                    errors.Add("Line " + row.LineNumber + ": Duplicate name '" + row.Name + "'.");
                }

                if (row.Type == CsvRowType.PLANET)
                {
                    planetNames.Add(row.Name);
                }
            }

            foreach (var row in rows)
            {
                if (row.Type == CsvRowType.MOON || row.Type == CsvRowType.RING)
                {
                    if (!planetNames.Contains(row.ParentName) && !existingPlanetNames.Contains(row.ParentName))
                    {
                        errors.Add("Line " + row.LineNumber + ": ParentName '" + row.ParentName +
                            "' does not match any PLANET in the CSV or existing system.");
                    }
                }
            }

            m_rows = rows;
            return errors.Count == 0;
        }

        /// <summary>
        /// Starts importing objects from a previously validated CSV file.
        /// Objects are added sequentially: planets and belts first, then moons and rings.
        /// </summary>
        /// <param name="progressCallback">Called with status messages during import</param>
        /// <param name="completionCallback">Called when import finishes (success, summary message)</param>
        public void StartImport(Action<string> progressCallback, Action<bool, string> completionCallback)
        {
            if (m_rows == null || m_rows.Count == 0)
            {
                completionCallback?.Invoke(false, "No rows to import. Validate the file first.");
                return;
            }

            m_progressCallback = progressCallback;
            m_completionCallback = completionCallback;
            m_processedRows = 0;
            m_failedRows = 0;
            m_failedNames = new List<string>();
            m_cancelled = false;

            // Pre-populate name-to-id map with existing system objects
            m_nameToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var starSystem = MyStarSystemGenerator.Static?.StarSystem;
            if (starSystem != null)
            {
                starSystem.Foreach((depth, obj) =>
                {
                    if (!string.IsNullOrEmpty(obj.DisplayName) && !m_nameToId.ContainsKey(obj.DisplayName))
                    {
                        m_nameToId[obj.DisplayName] = obj.Id;
                    }
                });
            }

            // Build import queue: parents first (PLANET, BELT), then children (MOON, RING)
            m_importQueue = new Queue<CsvRow>();
            foreach (var row in m_rows)
            {
                if (row.Type == CsvRowType.PLANET || row.Type == CsvRowType.BELT)
                    m_importQueue.Enqueue(row);
            }
            foreach (var row in m_rows)
            {
                if (row.Type == CsvRowType.MOON || row.Type == CsvRowType.RING)
                    m_importQueue.Enqueue(row);
            }

            m_totalRows = m_importQueue.Count;
            m_progressCallback?.Invoke("Starting import of " + m_totalRows + " objects...");

            ProcessNextRow();
        }

        /// <summary>
        /// Cancels an in-progress import. The current operation will complete
        /// but no further rows will be processed.
        /// </summary>
        public void Cancel()
        {
            m_cancelled = true;
        }

        private void ProcessNextRow()
        {
            if (m_cancelled)
            {
                m_completionCallback?.Invoke(false, "Import cancelled. " + m_processedRows + " of " + m_totalRows + " objects processed.");
                return;
            }

            if (m_importQueue.Count == 0)
            {
                string summary = "Import complete. " + (m_processedRows - m_failedRows) + " of " + m_totalRows + " objects added successfully.";
                if (m_failedRows > 0)
                {
                    summary += " " + m_failedRows + " failed: " + string.Join(", ", m_failedNames);
                }
                m_completionCallback?.Invoke(m_failedRows == 0, summary);
                return;
            }

            CsvRow row = m_importQueue.Dequeue();
            m_processedRows++;
            m_progressCallback?.Invoke("Importing " + row.Name + " (" + m_processedRows + "/" + m_totalRows + ")");

            switch (row.Type)
            {
                case CsvRowType.PLANET:
                    ImportPlanet(row);
                    break;
                case CsvRowType.MOON:
                    ImportMoon(row);
                    break;
                case CsvRowType.RING:
                    ImportRing(row);
                    break;
                case CsvRowType.BELT:
                    ImportBelt(row);
                    break;
            }
        }

        private void ImportPlanet(CsvRow row)
        {
            var planet = new MySystemPlanet()
            {
                DisplayName = row.Name,
                SubtypeId = row.SubtypeId,
                Diameter = row.Diameter,
                CenterPosition = row.Position,
                Generated = false
            };

            MyStarSystemGenerator.Static.AddObjectToSystem(planet, null, success =>
            {
                if (success)
                {
                    m_nameToId[row.Name] = planet.Id;
                    MyPluginLog.Log("CSV Import: Added planet '" + row.Name + "'");
                }
                else
                {
                    HandleFailure(row);
                }
                ProcessNextRow();
            });
        }

        private void ImportMoon(CsvRow row)
        {
            Guid parentId;
            if (!m_nameToId.TryGetValue(row.ParentName, out parentId))
            {
                MyPluginLog.Log("CSV Import: Parent '" + row.ParentName + "' not found for moon '" + row.Name + "'", LogLevel.WARNING);
                HandleFailure(row);
                ProcessNextRow();
                return;
            }

            var moon = new MySystemPlanetMoon()
            {
                DisplayName = row.Name,
                SubtypeId = row.SubtypeId,
                Diameter = row.Diameter,
                CenterPosition = row.Position,
                Generated = false
            };

            MyStarSystemGenerator.Static.AddObjectToSystem(moon, parentId, success =>
            {
                if (success)
                {
                    m_nameToId[row.Name] = moon.Id;
                    MyPluginLog.Log("CSV Import: Added moon '" + row.Name + "'");
                }
                else
                {
                    HandleFailure(row);
                }
                ProcessNextRow();
            });
        }

        private void ImportRing(CsvRow row)
        {
            Guid parentId;
            if (!m_nameToId.TryGetValue(row.ParentName, out parentId))
            {
                MyPluginLog.Log("CSV Import: Parent '" + row.ParentName + "' not found for ring '" + row.Name + "'", LogLevel.WARNING);
                HandleFailure(row);
                ProcessNextRow();
                return;
            }

            ImportAsteroidRing(row, parentId);
        }

        private void ImportBelt(CsvRow row)
        {
            var starSystem = MyStarSystemGenerator.Static.StarSystem;
            Guid parentId = starSystem.CenterObject != null ? starSystem.CenterObject.Id : Guid.Empty;

            ImportAsteroidRing(row, parentId);
        }

        private void ImportAsteroidRing(CsvRow row, Guid parentId)
        {
            var roid = new MySystemAsteroids()
            {
                DisplayName = row.Name,
                AsteroidTypeName = MyAsteroidRingProvider.TYPE_NAME,
                CenterPosition = row.Position,
                ParentId = parentId,
                AsteroidSize = new MySerializableMinMax(row.AsteroidSizeMin, row.AsteroidSizeMax)
            };

            var data = new MyAsteroidRingData()
            {
                Radius = row.RingRadius,
                Width = row.RingWidth,
                Height = row.RingHeight,
                CenterPosition = row.Position,
                AngleDegrees = row.RingAngle
            };

            MyAsteroidRingProvider.Static.AddInstance(roid, data, success =>
            {
                if (success)
                {
                    m_nameToId[row.Name] = roid.Id;
                    MyPluginLog.Log("CSV Import: Added asteroid object '" + row.Name + "'");
                }
                else
                {
                    HandleFailure(row);
                }
                ProcessNextRow();
            });
        }

        private void HandleFailure(CsvRow row)
        {
            m_failedRows++;
            m_failedNames.Add(row.Name);
            MyPluginLog.Log("CSV Import: Failed to import '" + row.Name + "' (line " + row.LineNumber + ")", LogLevel.WARNING);
        }

        /// <summary>
        /// Parses a CSV file into a list of CsvRow objects.
        /// Supports header-based column mapping, comment lines (#), and quoted fields.
        /// </summary>
        private List<CsvRow> ParseCsv(string filePath, List<string> errors)
        {
            string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
            var rows = new List<CsvRow>();

            if (lines.Length == 0)
            {
                errors.Add("CSV file is empty.");
                return rows;
            }

            // Find the header line (first non-empty, non-comment line)
            int headerIndex = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("#"))
                {
                    headerIndex = i;
                    break;
                }
            }

            if (headerIndex < 0)
            {
                errors.Add("No header row found in CSV file.");
                return rows;
            }

            // Parse header
            string[] headers = SplitCsvLine(lines[headerIndex]);
            var columnMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
            {
                string header = headers[i].Trim();
                if (header.Length > 0)
                    columnMap[header] = i;
            }

            // Verify required columns exist
            string[] requiredColumns = { "Type", "Name" };
            foreach (var col in requiredColumns)
            {
                if (!columnMap.ContainsKey(col))
                {
                    errors.Add("Missing required column: " + col);
                }
            }
            if (errors.Count > 0) return rows;

            // Parse data rows
            for (int i = headerIndex + 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                int lineNumber = i + 1; // 1-based line number
                string[] fields = SplitCsvLine(line);

                CsvRow row = new CsvRow();
                row.LineNumber = lineNumber;

                // Parse Type
                string typeStr = GetField(fields, columnMap, "Type").Trim().ToUpperInvariant();
                switch (typeStr)
                {
                    case "PLANET": row.Type = CsvRowType.PLANET; break;
                    case "MOON": row.Type = CsvRowType.MOON; break;
                    case "RING": row.Type = CsvRowType.RING; break;
                    case "BELT": row.Type = CsvRowType.BELT; break;
                    default:
                        errors.Add("Line " + lineNumber + ": Invalid Type '" + typeStr + "'. Must be PLANET, MOON, RING, or BELT.");
                        continue;
                }

                // Parse Name
                row.Name = GetField(fields, columnMap, "Name").Trim();
                if (string.IsNullOrEmpty(row.Name))
                {
                    errors.Add("Line " + lineNumber + ": Name is required.");
                    continue;
                }

                // Parse ParentName
                row.ParentName = GetField(fields, columnMap, "ParentName").Trim();

                // Validate required fields per type
                if (row.Type == CsvRowType.PLANET || row.Type == CsvRowType.MOON)
                {
                    row.SubtypeId = GetField(fields, columnMap, "SubtypeId").Trim();
                    if (string.IsNullOrEmpty(row.SubtypeId))
                    {
                        errors.Add("Line " + lineNumber + ": SubtypeId is required for " + row.Type + ".");
                        continue;
                    }

                    if (!TryParseDouble(fields, columnMap, "Diameter", out row.Diameter))
                    {
                        errors.Add("Line " + lineNumber + ": Diameter must be a valid number for " + row.Type + ".");
                        continue;
                    }

                    if (row.Diameter <= 0)
                    {
                        errors.Add("Line " + lineNumber + ": Diameter must be greater than 0.");
                        continue;
                    }
                }

                if (row.Type == CsvRowType.MOON || row.Type == CsvRowType.RING)
                {
                    if (string.IsNullOrEmpty(row.ParentName))
                    {
                        errors.Add("Line " + lineNumber + ": ParentName is required for " + row.Type + ".");
                        continue;
                    }
                }

                // Parse position
                double posX, posY, posZ;
                TryParseDouble(fields, columnMap, "PosX", out posX);
                TryParseDouble(fields, columnMap, "PosY", out posY);
                TryParseDouble(fields, columnMap, "PosZ", out posZ);
                row.Position = new Vector3D(posX, posY, posZ);

                // Parse ring/belt specific fields
                if (row.Type == CsvRowType.RING || row.Type == CsvRowType.BELT)
                {
                    if (!TryParseDouble(fields, columnMap, "RingRadius", out row.RingRadius) || row.RingRadius <= 0)
                    {
                        errors.Add("Line " + lineNumber + ": RingRadius must be a positive number for " + row.Type + ".");
                        continue;
                    }

                    if (!TryParseDouble(fields, columnMap, "RingWidth", out row.RingWidth) || row.RingWidth <= 0)
                    {
                        errors.Add("Line " + lineNumber + ": RingWidth must be a positive number for " + row.Type + ".");
                        continue;
                    }

                    if (!TryParseDouble(fields, columnMap, "RingHeight", out row.RingHeight) || row.RingHeight <= 0)
                    {
                        errors.Add("Line " + lineNumber + ": RingHeight must be a positive number for " + row.Type + ".");
                        continue;
                    }

                    double angleX, angleY, angleZ;
                    TryParseDouble(fields, columnMap, "RingAngleX", out angleX);
                    TryParseDouble(fields, columnMap, "RingAngleY", out angleY);
                    TryParseDouble(fields, columnMap, "RingAngleZ", out angleZ);
                    row.RingAngle = new Vector3D(angleX, angleY, angleZ);

                    long sizeMin, sizeMax;
                    if (!TryParseLong(fields, columnMap, "AsteroidSizeMin", out sizeMin))
                        sizeMin = 32;
                    if (!TryParseLong(fields, columnMap, "AsteroidSizeMax", out sizeMax))
                        sizeMax = 1024;
                    row.AsteroidSizeMin = sizeMin;
                    row.AsteroidSizeMax = sizeMax;
                }

                rows.Add(row);
            }

            return rows;
        }

        private string GetField(string[] fields, Dictionary<string, int> columnMap, string columnName)
        {
            int index;
            if (columnMap.TryGetValue(columnName, out index) && index < fields.Length)
                return fields[index];
            return "";
        }

        private bool TryParseDouble(string[] fields, Dictionary<string, int> columnMap, string columnName, out double value)
        {
            value = 0;
            string field = GetField(fields, columnMap, columnName).Trim();
            if (string.IsNullOrEmpty(field))
                return false;
            return double.TryParse(field, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private bool TryParseLong(string[] fields, Dictionary<string, int> columnMap, string columnName, out long value)
        {
            value = 0;
            string field = GetField(fields, columnMap, columnName).Trim();
            if (string.IsNullOrEmpty(field))
                return false;
            return long.TryParse(field, out value);
        }

        /// <summary>
        /// Splits a CSV line respecting double-quoted fields.
        /// </summary>
        private string[] SplitCsvLine(string line)
        {
            var fields = new List<string>();
            bool inQuotes = false;
            var current = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++; // Skip escaped quote
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == ',')
                    {
                        fields.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
            }

            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}
