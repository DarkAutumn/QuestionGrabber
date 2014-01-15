using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace QuestionGrabber
{

    public struct IniValue
    {
        string m_name, m_value;

        public string Name { get { return m_name; } }
        public string Value { get { return m_value; } }

        public IniValue(string name, string value)
        {
            m_name = name;
            m_value = value;
        }
    }

    public class IniSection
    {
        List<string> m_rawLines;
        Dictionary<string, string> m_pairs;

        /// <summary>
        /// The name of this section.  E.G., [SectionName].
        /// </summary>
        public string Name { get; private set; }

        public IniSection(string name, List<string> lines)
        {
            Name = name;
            m_rawLines = lines;
        }


        public IEnumerable<string> EnumerateRawStrings()
        {
            if (m_rawLines == null)
                return new string[0];

            return m_rawLines;
        }

        public string GetValue(string key)
        {
            InitPairs();

            string value;
            if (m_pairs.TryGetValue(key, out value))
                return value;

            return (from r in m_pairs where r.Key.Equals(key, StringComparison.InvariantCultureIgnoreCase) select r.Value).FirstOrDefault();
        }

        public IEnumerable<IniValue> EnumerateValuePairs()
        {
            InitPairs();

            foreach (var val in m_pairs)
                yield return new IniValue(val.Key, val.Value);
        }

        private void InitPairs()
        {
            if (m_pairs != null)
                return;

            m_pairs = new Dictionary<string, string>();
            if (m_rawLines == null)
                return;

            foreach (var line in m_rawLines)
            {
                int i = line.IndexOf('=');

                string name = line.Substring(0, i).Trim();
                string value = line.Substring(i + 1).Trim();

                m_pairs[name] = value;
            }
        }
    }

    public class IniReader
    {
        private List<IniSection> Sections { get; set; }

        public IniReader(string file)
        {
            Sections = new List<IniSection>();

            using (StreamReader reader = File.OpenText(file))
            {
                string section = null;
                List<string> values = new List<string>();

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();

                    int comment = line.IndexOf(";");
                    if (comment != -1)
                    {
                        line = line.Substring(0, comment);
                        line = line.TrimEnd();
                    }

                    int start = line.IndexOf('[');
                    int end = line.IndexOf(']');

                    if (start != -1 && end != -1)
                    {
                        if (start != 0 || end != line.Length - 1 || start == end - 1)
                            throw new FormatException(string.Format("Line '{0}' is not a valid section header.", line));

                        line = line.Substring(1, end - 1);

                        if (section != null)
                            Sections.Add(new IniSection(section, values));

                        section = line;
                        values = new List<string>();
                    }
                    else if (line.Length != 0)
                    {
                        values.Add(line);
                    }
                }

                if (section != null)
                    Sections.Add(new IniSection(section, values));
            }
        }

        public IniSection GetSectionByName(string name)
        {
            return (from s in Sections where s.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase) select s).FirstOrDefault();
        }
    }
}
