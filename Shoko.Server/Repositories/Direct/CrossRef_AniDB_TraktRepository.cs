﻿using System.Collections.Generic;
using Shoko.Models.Server;
using NHibernate;
using NHibernate.Criterion;
using Shoko.Server.Databases;
using Shoko.Server.Models;
using Shoko.Server.Obsolete;

namespace Shoko.Server.Repositories.Direct
{
#pragma warning disable CS0612 // Type or member is obsolete
    public class CrossRef_AniDB_TraktRepository : BaseDirectRepository<CrossRef_AniDB_Trakt, int>
    {
        private CrossRef_AniDB_TraktRepository()
        {
        }

        public static CrossRef_AniDB_TraktRepository Create()
        {
            return new CrossRef_AniDB_TraktRepository();
        }

        public CrossRef_AniDB_Trakt GetByAnimeID(int id)
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetByAnimeID(session, id);
            }
        }

        public CrossRef_AniDB_Trakt GetByAnimeID(ISession session, int id)
        {
            CrossRef_AniDB_Trakt cr = session
                .CreateCriteria(typeof(CrossRef_AniDB_Trakt))
                .Add(Restrictions.Eq("AnimeID", id))
                .UniqueResult<CrossRef_AniDB_Trakt>();
            return cr;
        }

        public CrossRef_AniDB_Trakt GetByTraktID(string id, int season)
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                CrossRef_AniDB_Trakt cr = session
                    .CreateCriteria(typeof(CrossRef_AniDB_Trakt))
                    .Add(Restrictions.Eq("TraktID", id))
                    .Add(Restrictions.Eq("TraktSeasonNumber", season))
                    .UniqueResult<CrossRef_AniDB_Trakt>();
                return cr;
            }
        }

        public List<CrossRef_AniDB_Trakt> GetByTraktID(string id)
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                var series = session
                    .CreateCriteria(typeof(CrossRef_AniDB_Trakt))
                    .Add(Restrictions.Eq("TraktID", id))
                    .List<CrossRef_AniDB_Trakt>();

                return new List<CrossRef_AniDB_Trakt>(series);
            }
        }
    }
#pragma warning restore CS0612 // Type or member is obsolete
}