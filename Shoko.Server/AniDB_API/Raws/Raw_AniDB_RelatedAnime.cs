﻿using System;
using System.Xml;

namespace AniDBAPI
{
    [Serializable]
    public class Raw_AniDB_RelatedAnime : XMLBase
    {
        public int AnimeID { get; set; }
        public int RelatedAnimeID { get; set; }
        public string RelationType { get; set; }

        public Raw_AniDB_RelatedAnime()
        {
            InitFields();
        }

        private void InitFields()
        {
            AnimeID = 0;
            RelatedAnimeID = 0;
            RelationType = string.Empty;
        }

        public void ProcessFromHTTPResult(XmlNode node, int anid)
        {
            InitFields();

            this.AnimeID = anid;

            int.TryParse(AniDBHTTPHelper.TryGetAttribute(node, "id"), out int id);
            this.RelatedAnimeID = id;

            this.RelationType = AniDBHTTPHelper.TryGetAttribute(node, "type");
        }
    }
}