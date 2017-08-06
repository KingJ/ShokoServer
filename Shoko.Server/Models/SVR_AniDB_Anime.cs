﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Xml.Serialization;
using AniDBAPI;
using Shoko.Models.Azure;
using NHibernate;
using NLog;
using Shoko.Commons.Extensions;
using Shoko.Commons.Utils;
using Shoko.Models;
using Shoko.Models.Client;
using Shoko.Models.Enums;
using Shoko.Models.Server;
using Shoko.Server.Commands;
using Shoko.Server.Databases;
using Shoko.Server.Extensions;
using Shoko.Server.ImageDownload;
using Shoko.Server.LZ4;
using Shoko.Server.Properties;
using Shoko.Server.Repositories;
using Shoko.Server.Repositories.Direct;
using Shoko.Server.Repositories.NHibernate;
using Shoko.Server.Tasks;
using Pri.LongPath;

namespace Shoko.Server.Models
{
    public class SVR_AniDB_Anime : AniDB_Anime
    {
        #region DB columns

        public int ContractVersion { get; set; }
        public byte[] ContractBlob { get; set; }
        public int ContractSize { get; set; }

        public const int CONTRACT_VERSION = 7;

        #endregion

        private CL_AniDB_AnimeDetailed _contract = null;

        public virtual CL_AniDB_AnimeDetailed Contract
        {
            get
            {
                if ((_contract == null) && (ContractBlob != null) && (ContractBlob.Length > 0) && (ContractSize > 0))
                    _contract = CompressionHelper.DeserializeObject<CL_AniDB_AnimeDetailed>(ContractBlob,
                        ContractSize);
                return _contract;
            }
            set
            {
                _contract = value;
                ContractBlob = CompressionHelper.SerializeObject(value, out int outsize);
                ContractSize = outsize;
                ContractVersion = CONTRACT_VERSION;
            }
        }

        public void CollectContractMemory()
        {
            _contract = null;
        }

        private static Logger logger = LogManager.GetCurrentClassLogger();

        // these files come from AniDB but we don't directly save them
        private string reviewIDListRAW;

        public static IList<string> GetAllReleaseGroups()
        {
            string query =
                @"SELECT Anime_GroupName
FROM AniDB_File
GROUP BY Anime_GroupName
ORDER BY count(DISTINCT AnimeID) DESC, Anime_GroupName ASC";
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                IList<string> result = session.CreateSQLQuery(query).List<string>();
                if (result.Contains("raw/unknown")) result.Remove("raw/unknown");
                return result;
            }
        }


        [XmlIgnore]
        public string PosterPath
        {
            get
            {
                if (String.IsNullOrEmpty(Picname)) return "";

                return Path.Combine(ImageUtils.GetAniDBImagePath(AnimeID), Picname);
            }
        }

        public List<TvDB_Episode> GetTvDBEpisodes()
        {
            List<TvDB_Episode> results = new List<TvDB_Episode>();
            int id = GetCrossRefTvDBV2()?.FirstOrDefault()?.TvDBID ?? -1;
            if (id != -1)
                results.AddRange(RepoFactory.TvDB_Episode.GetBySeriesID(id).OrderBy(a => a.SeasonNumber)
                    .ThenBy(a => a.EpisodeNumber));
            return results;
        }

        private Dictionary<int, TvDB_Episode> dictTvDBEpisodes = null;
        public Dictionary<int, TvDB_Episode> GetDictTvDBEpisodes()
        {
            if (dictTvDBEpisodes == null)
            {
                try
                {
                    List<TvDB_Episode> tvdbEpisodes = GetTvDBEpisodes();
                    if (tvdbEpisodes != null)
                    {
                        dictTvDBEpisodes = new Dictionary<int, TvDB_Episode>();
                        // create a dictionary of absolute episode numbers for tvdb episodes
                        // sort by season and episode number
                        // ignore season 0, which is used for specials

                        int i = 1;
                        foreach (TvDB_Episode ep in tvdbEpisodes)
                        {
                            dictTvDBEpisodes[i] = ep;
                            i++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, ex.ToString());
                }
            }
            return dictTvDBEpisodes;
        }

        private Dictionary<int, int> dictTvDBSeasons = null;
        public Dictionary<int, int> GetDictTvDBSeasons()
        {
            if (dictTvDBSeasons == null)
            {
                try
                {
                    dictTvDBSeasons = new Dictionary<int, int>();
                    // create a dictionary of season numbers and the first episode for that season
                    int i = 1;
                    int lastSeason = -999;
                    foreach (TvDB_Episode ep in GetTvDBEpisodes())
                    {
                        if (ep.SeasonNumber != lastSeason)
                            dictTvDBSeasons[ep.SeasonNumber] = i;

                        lastSeason = ep.SeasonNumber;
                        i++;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, ex.ToString());
                }
            }
            return dictTvDBSeasons;
        }

        private Dictionary<int, int> dictTvDBSeasonsSpecials = null;
        public Dictionary<int, int> GetDictTvDBSeasonsSpecials()
        {
            if (dictTvDBSeasonsSpecials == null)
            {
                try
                {
                    dictTvDBSeasonsSpecials = new Dictionary<int, int>();
                    // create a dictionary of season numbers and the first episode for that season
                    int i = 1;
                    int lastSeason = -999;
                    foreach (TvDB_Episode ep in GetTvDBEpisodes())
                    {
                        if (ep.SeasonNumber > 0) continue;

                        int thisSeason = 0;
                        if (ep.AirsBeforeSeason.HasValue) thisSeason = ep.AirsBeforeSeason.Value;
                        if (ep.AirsAfterSeason.HasValue) thisSeason = ep.AirsAfterSeason.Value;

                        if (thisSeason != lastSeason)
                            dictTvDBSeasonsSpecials[thisSeason] = i;

                        lastSeason = thisSeason;
                        i++;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, ex.ToString());
                }
            }
            return dictTvDBSeasonsSpecials;
        }

        public List<CrossRef_AniDB_TvDB_Episode> GetCrossRefTvDBEpisodes()
        {
            return RepoFactory.CrossRef_AniDB_TvDB_Episode.GetByAnimeID(AnimeID);
        }

        public List<CrossRef_AniDB_TvDBV2> GetCrossRefTvDBV2()
        {
            return RepoFactory.CrossRef_AniDB_TvDBV2.GetByAnimeID(this.AnimeID);
        }

        public List<CrossRef_AniDB_TraktV2> GetCrossRefTraktV2()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetCrossRefTraktV2(session);
            }
        }

        public List<CrossRef_AniDB_TraktV2> GetCrossRefTraktV2(ISession session)
        {
            return RepoFactory.CrossRef_AniDB_TraktV2.GetByAnimeID(session, this.AnimeID);
        }

        public List<CrossRef_AniDB_MAL> GetCrossRefMAL()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetCrossRefMAL(session);
            }
        }

        public List<CrossRef_AniDB_MAL> GetCrossRefMAL(ISession session)
        {
            return RepoFactory.CrossRef_AniDB_MAL.GetByAnimeID(session, this.AnimeID);
        }

        public TvDB_Series GetTvDBSeries()
        {
            int id = GetCrossRefTvDBV2()?.FirstOrDefault()?.TvDBID ?? -1;
            if (id == -1) return null;
            return RepoFactory.TvDB_Series.GetByTvDBID(id);
        }

        public List<TvDB_ImageFanart> GetTvDBImageFanarts()
        {
            List<TvDB_ImageFanart> results = new List<TvDB_ImageFanart>();
            int id = GetCrossRefTvDBV2()?.FirstOrDefault()?.TvDBID ?? -1;
            if (id != -1)
                results.AddRange(RepoFactory.TvDB_ImageFanart.GetBySeriesID(id));
            return results;
        }

        public List<TvDB_ImagePoster> GetTvDBImagePosters()
        {
            List<TvDB_ImagePoster> results = new List<TvDB_ImagePoster>();
            int id = GetCrossRefTvDBV2()?.FirstOrDefault()?.TvDBID ?? -1;
            if (id != -1)
                results.AddRange(RepoFactory.TvDB_ImagePoster.GetBySeriesID(id));
            return results;
        }

        public List<TvDB_ImageWideBanner> GetTvDBImageWideBanners()
        {
            List<TvDB_ImageWideBanner> results = new List<TvDB_ImageWideBanner>();
            int id = GetCrossRefTvDBV2()?.FirstOrDefault()?.TvDBID ?? -1;
            if (id != -1)
                results.AddRange(RepoFactory.TvDB_ImageWideBanner.GetBySeriesID(id));
            return results;
        }

        public CrossRef_AniDB_Other GetCrossRefMovieDB()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetCrossRefMovieDB(session.Wrap());
            }
        }

        public CrossRef_AniDB_Other GetCrossRefMovieDB(ISessionWrapper criteriaFactory)
        {
            return RepoFactory.CrossRef_AniDB_Other.GetByAnimeIDAndType(criteriaFactory, this.AnimeID,
                CrossRefType.MovieDB);
        }


        public MovieDB_Movie GetMovieDBMovie()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetMovieDBMovie(session.Wrap());
            }
        }

        public MovieDB_Movie GetMovieDBMovie(ISessionWrapper criteriaFactory)
        {
            CrossRef_AniDB_Other xref = GetCrossRefMovieDB(criteriaFactory);
            if (xref == null) return null;
            return RepoFactory.MovieDb_Movie.GetByOnlineID(criteriaFactory, Int32.Parse(xref.CrossRefID));
        }

        public List<MovieDB_Fanart> GetMovieDBFanarts()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetMovieDBFanarts(session.Wrap());
            }
        }

        public List<MovieDB_Fanart> GetMovieDBFanarts(ISessionWrapper session)
        {
            CrossRef_AniDB_Other xref = GetCrossRefMovieDB(session);
            if (xref == null) return new List<MovieDB_Fanart>();

            return RepoFactory.MovieDB_Fanart.GetByMovieID(session, Int32.Parse(xref.CrossRefID));
        }

        public List<MovieDB_Poster> GetMovieDBPosters()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetMovieDBPosters(session.Wrap());
            }
        }

        public List<MovieDB_Poster> GetMovieDBPosters(ISessionWrapper session)
        {
            CrossRef_AniDB_Other xref = GetCrossRefMovieDB(session);
            if (xref == null) return new List<MovieDB_Poster>();

            return RepoFactory.MovieDB_Poster.GetByMovieID(session, Int32.Parse(xref.CrossRefID));
        }

        public AniDB_Anime_DefaultImage GetDefaultPoster()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetDefaultPoster(session.Wrap());
            }
        }

        public AniDB_Anime_DefaultImage GetDefaultPoster(ISessionWrapper criteriaFactory)
        {
            return RepoFactory.AniDB_Anime_DefaultImage.GetByAnimeIDAndImagezSizeType(criteriaFactory, this.AnimeID,
                (int) ImageSizeType.Poster);
        }

        public string PosterPathNoDefault
        {
            get
            {
                string fileName = Path.Combine(ImageUtils.GetAniDBImagePath(AnimeID), Picname);
                return fileName;
            }
        }


        public string GetDefaultPosterPathNoBlanks()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetDefaultPosterPathNoBlanks(session.Wrap());
            }
        }

        public string GetDefaultPosterPathNoBlanks(ISessionWrapper session)
        {
            AniDB_Anime_DefaultImage defaultPoster = GetDefaultPoster(session);
            if (defaultPoster == null)
                return PosterPathNoDefault;
            else
            {
                ImageEntityType imageType = (ImageEntityType) defaultPoster.ImageParentType;

                switch (imageType)
                {
                    case ImageEntityType.AniDB_Cover:
                        return this.PosterPath;

                    case ImageEntityType.TvDB_Cover:
                        TvDB_ImagePoster tvPoster =
                            RepoFactory.TvDB_ImagePoster.GetByID(session, defaultPoster.ImageParentID);
                        if (tvPoster != null)
                            return tvPoster.GetFullImagePath();
                        else
                            return this.PosterPath;

                    case ImageEntityType.Trakt_Poster:
                        Trakt_ImagePoster traktPoster =
                            RepoFactory.Trakt_ImagePoster.GetByID(session, defaultPoster.ImageParentID);
                        if (traktPoster != null)
                            return traktPoster.GetFullImagePath();
                        else
                            return this.PosterPath;

                    case ImageEntityType.MovieDB_Poster:
                        MovieDB_Poster moviePoster =
                            RepoFactory.MovieDB_Poster.GetByID(session, defaultPoster.ImageParentID);
                        if (moviePoster != null)
                            return moviePoster.GetFullImagePath();
                        else
                            return this.PosterPath;
                }
            }

            return PosterPath;
        }

        public ImageDetails GetDefaultPosterDetailsNoBlanks()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetDefaultPosterDetailsNoBlanks(session.Wrap());
            }
        }

        public ImageDetails GetDefaultPosterDetailsNoBlanks(ISessionWrapper session)
        {
            ImageDetails details = new ImageDetails() {ImageType = JMMImageType.AniDB_Cover, ImageID = this.AnimeID};
            AniDB_Anime_DefaultImage defaultPoster = GetDefaultPoster(session);

            if (defaultPoster == null)
                return details;
            else
            {
                ImageEntityType imageType = (ImageEntityType) defaultPoster.ImageParentType;

                switch (imageType)
                {
                    case ImageEntityType.AniDB_Cover:
                        return details;

                    case ImageEntityType.TvDB_Cover:
                        TvDB_ImagePoster tvPoster =
                            RepoFactory.TvDB_ImagePoster.GetByID(session, defaultPoster.ImageParentID);
                        if (tvPoster != null)
                            details = new ImageDetails()
                            {
                                ImageType = JMMImageType.TvDB_Cover,
                                ImageID = tvPoster.TvDB_ImagePosterID
                            };
                        return details;

                    case ImageEntityType.Trakt_Poster:
                        Trakt_ImagePoster traktPoster =
                            RepoFactory.Trakt_ImagePoster.GetByID(session, defaultPoster.ImageParentID);
                        if (traktPoster != null)
                            details = new ImageDetails()
                            {
                                ImageType = JMMImageType.Trakt_Poster,
                                ImageID = traktPoster.Trakt_ImagePosterID
                            };
                        return details;

                    case ImageEntityType.MovieDB_Poster:
                        MovieDB_Poster moviePoster =
                            RepoFactory.MovieDB_Poster.GetByID(session, defaultPoster.ImageParentID);
                        if (moviePoster != null)
                            details = new ImageDetails()
                            {
                                ImageType = JMMImageType.MovieDB_Poster,
                                ImageID = moviePoster.MovieDB_PosterID
                            };
                        return details;
                }
            }

            return details;
        }

        public AniDB_Anime_DefaultImage GetDefaultFanart()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetDefaultFanart(session.Wrap());
            }
        }

        public AniDB_Anime_DefaultImage GetDefaultFanart(ISessionWrapper factory)
        {
            return RepoFactory.AniDB_Anime_DefaultImage.GetByAnimeIDAndImagezSizeType(factory, this.AnimeID,
                (int) ImageSizeType.Fanart);
        }

        public ImageDetails GetDefaultFanartDetailsNoBlanks()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetDefaultFanartDetailsNoBlanks(session.Wrap());
            }
        }

        public ImageDetails GetDefaultFanartDetailsNoBlanks(ISessionWrapper session)
        {
            Random fanartRandom = new Random();

            ImageDetails details = null;
            if (GetDefaultFanart(session) == null)
            {
                List<CL_AniDB_Anime_DefaultImage> fanarts = Contract.AniDBAnime.Fanarts;
                if (fanarts == null || fanarts.Count == 0) return null;
                CL_AniDB_Anime_DefaultImage art = fanarts[fanartRandom.Next(0, fanarts.Count)];
                details = new ImageDetails()
                {
                    ImageID = art.AniDB_Anime_DefaultImageID,
                    ImageType = (JMMImageType) art.ImageType
                };
                return details;
            }
            else
            {
                // TODO Move this to contract as well
                AniDB_Anime_DefaultImage fanart = GetDefaultFanart();
                ImageEntityType imageType = (ImageEntityType) fanart.ImageParentType;

                switch (imageType)
                {
                    case ImageEntityType.TvDB_FanArt:
                        TvDB_ImageFanart tvFanart = RepoFactory.TvDB_ImageFanart.GetByID(session, fanart.ImageParentID);
                        if (tvFanart != null)
                            details = new ImageDetails()
                            {
                                ImageType = JMMImageType.TvDB_FanArt,
                                ImageID = tvFanart.TvDB_ImageFanartID
                            };
                        return details;

                    case ImageEntityType.Trakt_Fanart:
                        Trakt_ImageFanart traktFanart =
                            RepoFactory.Trakt_ImageFanart.GetByID(session, fanart.ImageParentID);
                        if (traktFanart != null)
                            details = new ImageDetails()
                            {
                                ImageType = JMMImageType.Trakt_Fanart,
                                ImageID = traktFanart.Trakt_ImageFanartID
                            };
                        return details;

                    case ImageEntityType.MovieDB_FanArt:
                        MovieDB_Fanart movieFanart = RepoFactory.MovieDB_Fanart.GetByID(session, fanart.ImageParentID);
                        if (movieFanart != null)
                            details = new ImageDetails()
                            {
                                ImageType = JMMImageType.MovieDB_FanArt,
                                ImageID = movieFanart.MovieDB_FanartID
                            };
                        return details;
                }
            }

            return null;
        }

        public string GetDefaultFanartOnlineURL()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetDefaultFanartOnlineURL(session.Wrap());
            }
        }

        public string GetDefaultFanartOnlineURL(ISessionWrapper session)
        {
            Random fanartRandom = new Random();


            if (GetDefaultFanart() == null)
            {
                // get a random fanart
                if (this.GetAnimeTypeEnum() == Shoko.Models.Enums.AnimeType.Movie)
                {
                    List<MovieDB_Fanart> fanarts = GetMovieDBFanarts(session);
                    if (fanarts.Count == 0) return "";

                    MovieDB_Fanart movieFanart = fanarts[fanartRandom.Next(0, fanarts.Count)];
                    return movieFanart.URL;
                }
                else
                {
                    List<TvDB_ImageFanart> fanarts = GetTvDBImageFanarts();
                    if (fanarts.Count == 0) return null;

                    TvDB_ImageFanart tvFanart = fanarts[fanartRandom.Next(0, fanarts.Count)];
                    return String.Format(Constants.URLS.TvDB_Images, tvFanart.BannerPath);
                }
            }
            else
            {
                // TODO Move this to contract as well
                AniDB_Anime_DefaultImage fanart = GetDefaultFanart();
                ImageEntityType imageType = (ImageEntityType) fanart.ImageParentType;

                switch (imageType)
                {
                    case ImageEntityType.TvDB_FanArt:
                        TvDB_ImageFanart tvFanart =
                            RepoFactory.TvDB_ImageFanart.GetByID(GetDefaultFanart(session).ImageParentID);
                        if (tvFanart != null)
                            return String.Format(Constants.URLS.TvDB_Images, tvFanart.BannerPath);
                        break;

                    case ImageEntityType.Trakt_Fanart:
                        Trakt_ImageFanart traktFanart =
                            RepoFactory.Trakt_ImageFanart.GetByID(GetDefaultFanart(session).ImageParentID);
                        if (traktFanart != null)
                            return traktFanart.ImageURL;
                        break;

                    case ImageEntityType.MovieDB_FanArt:
                        MovieDB_Fanart movieFanart =
                            RepoFactory.MovieDB_Fanart.GetByID(GetDefaultFanart(session).ImageParentID);
                        if (movieFanart != null)
                            return movieFanart.URL;
                        break;
                }
            }

            return "";
        }

        public AniDB_Anime_DefaultImage GetDefaultWideBanner()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetDefaultWideBanner(session.Wrap());
            }
        }

        public AniDB_Anime_DefaultImage GetDefaultWideBanner(ISessionWrapper session)
        {
            return RepoFactory.AniDB_Anime_DefaultImage.GetByAnimeIDAndImagezSizeType(session, this.AnimeID,
                (int) ImageSizeType.WideBanner);
        }

        public ImageDetails GetDefaultWideBannerDetailsNoBlanks()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetDefaultWideBannerDetailsNoBlanks(session.Wrap());
            }
        }

        public ImageDetails GetDefaultWideBannerDetailsNoBlanks(ISessionWrapper session)
        {
            Random bannerRandom = new Random();

            ImageDetails details = null;
            AniDB_Anime_DefaultImage banner = GetDefaultWideBanner();
            if (banner == null)
            {
                // get a random banner (only tvdb)
                if (this.GetAnimeTypeEnum() == Shoko.Models.Enums.AnimeType.Movie)
                {
                    // MovieDB doesn't have banners
                    return null;
                }
                else
                {
                    List<CL_AniDB_Anime_DefaultImage> banners = Contract.AniDBAnime.Banners;
                    if (banners == null || banners.Count == 0) return null;
                    CL_AniDB_Anime_DefaultImage art = banners[bannerRandom.Next(0, banners.Count)];
                    details = new ImageDetails()
                    {
                        ImageID = art.AniDB_Anime_DefaultImageID,
                        ImageType = (JMMImageType) art.ImageType
                    };
                    return details;
                }
            }
            else
            {
                ImageEntityType imageType = (ImageEntityType) banner.ImageParentType;

                switch (imageType)
                {
                    case ImageEntityType.TvDB_Banner:
                        details = new ImageDetails()
                        {
                            ImageType = JMMImageType.TvDB_Banner,
                            ImageID = banner.ToClient(session).TVWideBanner.TvDB_ImageWideBannerID
                        };
                        return details;
                }
            }

            return null;
        }


        [XmlIgnore]
        public string TagsString
        {
            get
            {
                List<AniDB_Tag> tags = GetTags();
                string temp = "";
                foreach (AniDB_Tag tag in tags)
                    temp += tag.TagName + "|";
                if (temp.Length > 2)
                    temp = temp.Substring(0, temp.Length - 2);
                return temp;
            }
        }


        public List<AniDB_Tag> GetTags()
        {
            List<AniDB_Tag> tags = new List<AniDB_Tag>();
            foreach (AniDB_Anime_Tag tag in GetAnimeTags())
            {
                AniDB_Tag newTag = RepoFactory.AniDB_Tag.GetByTagID(tag.TagID);
                if (newTag != null) tags.Add(newTag);
            }
            return tags;
        }

        /*public List<AniDB_Anime_Tag> GetAnimeTags()
		{
			using (var session = JMMService.SessionFactory.OpenSession())
			{
                return GetAnimeTags(session);
			}
		}

        public List<AniDB_Anime_Tag> GetAnimeTags(ISession session)
		{
            AniDB_Anime_TagRepository repTagXRef = new AniDB_Anime_TagRepository();
			return repTagXRef.GetByAnimeID(session, AnimeID);
		}

        public List<AniDB_Tag> GetAniDBTags(ISession session)
		{
            AniDB_TagRepository repCats = new AniDB_TagRepository();
			return repCats.GetByAnimeID(session, AnimeID);
		}
        */

        public List<CustomTag> GetCustomTagsForAnime()
        {
            return RepoFactory.CustomTag.GetByAnimeID(AnimeID);
        }

        public List<AniDB_Tag> GetAniDBTags()
        {
            return RepoFactory.AniDB_Tag.GetByAnimeID(AnimeID);
        }

        public List<AniDB_Anime_Tag> GetAnimeTags()
        {
            return RepoFactory.AniDB_Anime_Tag.GetByAnimeID(AnimeID);
        }

        public List<AniDB_Anime_Relation> GetRelatedAnime()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetRelatedAnime(session.Wrap());
            }
        }

        public List<AniDB_Anime_Relation> GetRelatedAnime(ISessionWrapper session)
        {
            return RepoFactory.AniDB_Anime_Relation.GetByAnimeID(session, AnimeID);
        }

        public List<AniDB_Anime_Similar> GetSimilarAnime()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetSimilarAnime(session);
            }
        }

        public List<AniDB_Anime_Similar> GetSimilarAnime(ISession session)
        {
            return RepoFactory.AniDB_Anime_Similar.GetByAnimeID(session, AnimeID);
        }

        [XmlIgnore]
        public List<AniDB_Anime_Review> AnimeReviews
        {
            get { return RepoFactory.AniDB_Anime_Review.GetByAnimeID(AnimeID); }
        }

        public List<SVR_AniDB_Anime> GetAllRelatedAnime()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetAllRelatedAnime(session.Wrap());
            }
        }

        public List<SVR_AniDB_Anime> GetAllRelatedAnime(ISessionWrapper session)
        {
            List<SVR_AniDB_Anime> relList = new List<SVR_AniDB_Anime>();
            List<int> relListIDs = new List<int>();
            List<int> searchedIDs = new List<int>();

            GetRelatedAnimeRecursive(session, this.AnimeID, ref relList, ref relListIDs, ref searchedIDs);
            return relList;
        }

        public List<AniDB_Anime_Character> GetAnimeCharacters()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return GetAnimeCharacters(session.Wrap());
            }
        }

        public List<AniDB_Anime_Character> GetAnimeCharacters(ISessionWrapper session)
        {
            return RepoFactory.AniDB_Anime_Character.GetByAnimeID(session, AnimeID);
        }

        public List<AniDB_Anime_Title> GetTitles()
        {
            return RepoFactory.AniDB_Anime_Title.GetByAnimeID(AnimeID);
        }

        public string GetFormattedTitle(List<AniDB_Anime_Title> titles)
        {
            foreach (NamingLanguage nlan in Languages.PreferredNamingLanguages)
            {
                string thisLanguage = nlan.Language.Trim().ToUpper();

                // Romaji and English titles will be contained in MAIN and/or OFFICIAL
                // we won't use synonyms for these two languages
                if (thisLanguage.Equals(Shoko.Models.Constants.AniDBLanguageType.Romaji) ||
                    thisLanguage.Equals(Shoko.Models.Constants.AniDBLanguageType.English))
                {
                    foreach (AniDB_Anime_Title title in titles)
                    {
                        string titleType = title.TitleType.Trim().ToUpper();
                        // first try the  Main title
                        if (titleType == Shoko.Models.Constants.AnimeTitleType.Main.ToUpper() &&
                            title.Language.Trim().ToUpper() == thisLanguage)
                            return title.Title;
                    }
                }

                // now try the official title
                foreach (AniDB_Anime_Title title in titles)
                {
                    string titleType = title.TitleType.Trim().ToUpper();
                    if (titleType == Shoko.Models.Constants.AnimeTitleType.Official.ToUpper() &&
                        title.Language.Trim().ToUpper() == thisLanguage)
                        return title.Title;
                }

                // try synonyms
                if (ServerSettings.LanguageUseSynonyms)
                {
                    foreach (AniDB_Anime_Title title in titles)
                    {
                        string titleType = title.TitleType.Trim().ToUpper();
                        if (titleType == Shoko.Models.Constants.AnimeTitleType.Synonym.ToUpper() &&
                            title.Language.Trim().ToUpper() == thisLanguage)
                            return title.Title;
                    }
                }
            }

            // otherwise just use the main title
            return this.MainTitle;
        }

        public string GetFormattedTitle()
        {
            List<AniDB_Anime_Title> thisTitles = this.GetTitles();
            return GetFormattedTitle(thisTitles);
        }

        [XmlIgnore]
        public AniDB_Vote UserVote
        {
            get
            {
                try
                {
                    return RepoFactory.AniDB_Vote.GetByAnimeID(this.AnimeID);
                }
                catch (Exception ex)
                {
                    logger.Error("Error in  UserVote: {0}", ex.ToString());
                    return null;
                }
            }
        }

        public AniDB_Vote GetUserVote(ISession session)
        {
            try
            {
                return RepoFactory.AniDB_Vote.GetByAnimeID(session, this.AnimeID);
            }
            catch (Exception ex)
            {
                logger.Error("Error in  UserVote: {0}", ex.ToString());
                return null;
            }
        }


        public string PreferredTitle
        {
            get
            {
                List<AniDB_Anime_Title> titles = this.GetTitles();

                foreach (NamingLanguage nlan in Languages.PreferredNamingLanguages)
                {
                    string thisLanguage = nlan.Language.Trim().ToUpper();
                    // Romaji and English titles will be contained in MAIN and/or OFFICIAL
                    // we won't use synonyms for these two languages
                    if (thisLanguage == "X-JAT" || thisLanguage == "EN")
                    {
                        // first try the  Main title
                        for (int i = 0; i < titles.Count; i++)
                        {
                            if (titles[i].Language.Trim().ToUpper() == thisLanguage &&
                                titles[i].TitleType.Trim().ToUpper() ==
                                Shoko.Models.Constants.AnimeTitleType.Main.ToUpper())
                                return titles[i].Title;
                        }
                    }

                    // now try the official title
                    for (int i = 0; i < titles.Count; i++)
                    {
                        if (titles[i].Language.Trim().ToUpper() == thisLanguage &&
                            titles[i].TitleType.Trim().ToUpper() ==
                            Shoko.Models.Constants.AnimeTitleType.Official.ToUpper())
                            return titles[i].Title;
                    }

                    // try synonyms
                    if (ServerSettings.LanguageUseSynonyms)
                    {
                        for (int i = 0; i < titles.Count; i++)
                        {
                            if (titles[i].Language.Trim().ToUpper() == thisLanguage &&
                                titles[i].TitleType.Trim().ToUpper() ==
                                Shoko.Models.Constants.AnimeTitleType.Synonym.ToUpper())
                                return titles[i].Title;
                        }
                    }
                }

                // otherwise just use the main title
                for (int i = 0; i < titles.Count; i++)
                {
                    if (titles[i].TitleType.Trim().ToUpper() == Shoko.Models.Constants.AnimeTitleType.Main.ToUpper())
                        return titles[i].Title;
                }

                return "ERROR";
            }
        }


        [XmlIgnore]
        public List<AniDB_Episode> AniDBEpisodes => RepoFactory.AniDB_Episode.GetByAnimeID(AnimeID);

        public List<AniDB_Episode> GetAniDBEpisodes()
        {
            return RepoFactory.AniDB_Episode.GetByAnimeID(AnimeID);
        }

        public SVR_AniDB_Anime()
        {
            this.DisableExternalLinksFlag = 0;
        }

        private static bool Populate(SVR_AniDB_Anime adnidbanime, Raw_AniDB_Anime animeInfo)
        {
            // We need various values to be populated to be considered valid
            if (animeInfo.AnimeID <= 0 || string.IsNullOrEmpty(animeInfo.MainTitle)) return false;
            adnidbanime.AirDate = animeInfo.AirDate;
            adnidbanime.AllCinemaID = animeInfo.AllCinemaID;
            adnidbanime.AnimeID = animeInfo.AnimeID;
            //this.AnimeNfo = animeInfo.AnimeNfoID;
            adnidbanime.AnimePlanetID = animeInfo.AnimePlanetID;
            adnidbanime.SetAnimeTypeRAW(animeInfo.AnimeTypeRAW);
            adnidbanime.ANNID = animeInfo.ANNID;
            adnidbanime.AvgReviewRating = animeInfo.AvgReviewRating;
            adnidbanime.AwardList = animeInfo.AwardList;
            adnidbanime.BeginYear = animeInfo.BeginYear;
            adnidbanime.DateTimeDescUpdated = DateTime.Now;
            adnidbanime.DateTimeUpdated = DateTime.Now;
            adnidbanime.Description = animeInfo.Description ?? string.Empty;
            adnidbanime.EndDate = animeInfo.EndDate;
            adnidbanime.EndYear = animeInfo.EndYear;
            adnidbanime.MainTitle = animeInfo.MainTitle;
            adnidbanime.AllTitles = "";
            adnidbanime.AllTags = "";
            //this.EnglishName = animeInfo.EnglishName;
            adnidbanime.EpisodeCount = animeInfo.EpisodeCount;
            adnidbanime.EpisodeCountNormal = animeInfo.EpisodeCountNormal;
            adnidbanime.EpisodeCountSpecial = animeInfo.EpisodeCountSpecial;
            //this.genre
            adnidbanime.ImageEnabled = 1;
            //this.KanjiName = animeInfo.KanjiName;
            adnidbanime.LatestEpisodeNumber = animeInfo.LatestEpisodeNumber;
            //this.OtherName = animeInfo.OtherName;
            adnidbanime.Picname = animeInfo.Picname;
            adnidbanime.Rating = animeInfo.Rating;
            //this.relations
            adnidbanime.Restricted = animeInfo.Restricted;
            adnidbanime.ReviewCount = animeInfo.ReviewCount;
            //this.RomajiName = animeInfo.RomajiName;
            //this.ShortNames = animeInfo.ShortNames.Replace("'", "|");
            //this.Synonyms = animeInfo.Synonyms.Replace("'", "|");
            adnidbanime.TempRating = animeInfo.TempRating;
            adnidbanime.TempVoteCount = animeInfo.TempVoteCount;
            adnidbanime.URL = animeInfo.URL;
            adnidbanime.VoteCount = animeInfo.VoteCount;
            return true;
        }

        public void PopulateAndSaveFromHTTP(ISession session, Raw_AniDB_Anime animeInfo, List<Raw_AniDB_Episode> eps,
            List<Raw_AniDB_Anime_Title> titles,
            List<Raw_AniDB_Category> cats, List<Raw_AniDB_Tag> tags, List<Raw_AniDB_Character> chars,
            List<Raw_AniDB_RelatedAnime> rels, List<Raw_AniDB_SimilarAnime> sims,
            List<Raw_AniDB_Recommendation> recs, bool downloadRelations)
        {
            logger.Trace("------------------------------------------------");
            logger.Trace($"PopulateAndSaveFromHTTP: for {animeInfo.AnimeID} - {animeInfo.MainTitle}");
            logger.Trace("------------------------------------------------");

            Stopwatch taskTimer = new Stopwatch();
            Stopwatch totalTimer = Stopwatch.StartNew();

            if (!Populate(this, animeInfo))
            {
                logger.Error("AniDB_Anime was unable to populate as it received invalid info. " +
                             "This is not an error on our end. It is AniDB's issue, " +
                             "as they did not return either an ID or a title for the anime.");
                totalTimer.Stop();
                return;
            }

            // save now for FK purposes
            RepoFactory.AniDB_Anime.Save(this);

            taskTimer.Start();

            CreateEpisodes(eps);
            taskTimer.Stop();
            logger.Trace("CreateEpisodes in : " + taskTimer.ElapsedMilliseconds);
            taskTimer.Restart();

            CreateTitles(titles);
            taskTimer.Stop();
            logger.Trace("CreateTitles in : " + taskTimer.ElapsedMilliseconds);
            taskTimer.Restart();

            CreateTags(tags);
            taskTimer.Stop();
            logger.Trace("CreateTags in : " + taskTimer.ElapsedMilliseconds);
            taskTimer.Restart();

            CreateCharacters(session, chars);
            taskTimer.Stop();
            logger.Trace("CreateCharacters in : " + taskTimer.ElapsedMilliseconds);
            taskTimer.Restart();

            CreateRelations(session, rels, downloadRelations);
            taskTimer.Stop();
            logger.Trace("CreateRelations in : " + taskTimer.ElapsedMilliseconds);
            taskTimer.Restart();

            CreateSimilarAnime(session, sims);
            taskTimer.Stop();
            logger.Trace("CreateSimilarAnime in : " + taskTimer.ElapsedMilliseconds);
            taskTimer.Restart();

            CreateRecommendations(session, recs);
            taskTimer.Stop();
            logger.Trace("CreateRecommendations in : " + taskTimer.ElapsedMilliseconds);
            taskTimer.Restart();

            RepoFactory.AniDB_Anime.Save(this);
            totalTimer.Stop();
            logger.Trace("TOTAL TIME in : " + totalTimer.ElapsedMilliseconds);
            logger.Trace("------------------------------------------------");
        }

        /// <summary>
        /// we are depending on the HTTP api call to get most of the info
        /// we only use UDP to get mssing information
        /// </summary>
        /// <param name="animeInfo"></param>
        public void PopulateAndSaveFromUDP(Raw_AniDB_Anime animeInfo)
        {
            // raw fields
            this.reviewIDListRAW = animeInfo.ReviewIDListRAW;

            // save now for FK purposes
            RepoFactory.AniDB_Anime.Save(this);

            CreateAnimeReviews();
        }

        private void CreateEpisodes(List<Raw_AniDB_Episode> eps)
        {
            if (eps == null) return;


            this.EpisodeCountSpecial = 0;
            this.EpisodeCountNormal = 0;

            List<SVR_AnimeEpisode> animeEpsToDelete = new List<SVR_AnimeEpisode>();
            List<AniDB_Episode> aniDBEpsToDelete = new List<AniDB_Episode>();

            foreach (Raw_AniDB_Episode epraw in eps)
            {
                //
                // we need to do this check because some times AniDB will replace an existing episode with a new episode
                List<AniDB_Episode> existingEps = RepoFactory.AniDB_Episode.GetByAnimeIDAndEpisodeTypeNumber(
                    epraw.AnimeID, (EpisodeType) epraw.EpisodeType, epraw.EpisodeNumber);

                // delete any old records
                foreach (AniDB_Episode epOld in existingEps)
                {
                    if (epOld.EpisodeID != epraw.EpisodeID)
                    {
                        // first delete any AnimeEpisode records that point to the new anidb episode
                        SVR_AnimeEpisode aniep = RepoFactory.AnimeEpisode.GetByAniDBEpisodeID(epOld.EpisodeID);
                        if (aniep != null)
                            animeEpsToDelete.Add(aniep);
                        aniDBEpsToDelete.Add(epOld);
                    }
                }
            }
            RepoFactory.AnimeEpisode.Delete(animeEpsToDelete);
            RepoFactory.AniDB_Episode.Delete(aniDBEpsToDelete);


            List<AniDB_Episode> epsToSave = new List<AniDB_Episode>();
            foreach (Raw_AniDB_Episode epraw in eps)
            {
                AniDB_Episode epNew = RepoFactory.AniDB_Episode.GetByEpisodeID(epraw.EpisodeID);
                if (epNew == null) epNew = new AniDB_Episode();

                epNew.Populate(epraw);
                epsToSave.Add(epNew);

                // since the HTTP api doesn't return a count of the number of specials, we will calculate it here
                if (epNew.GetEpisodeTypeEnum() == EpisodeType.Episode)
                    this.EpisodeCountNormal++;

                if (epNew.GetEpisodeTypeEnum() == EpisodeType.Special)
                    this.EpisodeCountSpecial++;
            }
            RepoFactory.AniDB_Episode.Save(epsToSave);

            this.EpisodeCount = EpisodeCountSpecial + EpisodeCountNormal;
        }

        private void CreateTitles(List<Raw_AniDB_Anime_Title> titles)
        {
            if (titles == null) return;

            this.AllTitles = "";

            List<AniDB_Anime_Title> titlesToDelete = RepoFactory.AniDB_Anime_Title.GetByAnimeID(AnimeID);
            List<AniDB_Anime_Title> titlesToSave = new List<AniDB_Anime_Title>();
            foreach (Raw_AniDB_Anime_Title rawtitle in titles)
            {
                AniDB_Anime_Title title = new AniDB_Anime_Title();
                if (!title.Populate(rawtitle)) continue;
                titlesToSave.Add(title);

                if (this.AllTitles.Length > 0) this.AllTitles += "|";
                this.AllTitles += rawtitle.Title;
            }
            RepoFactory.AniDB_Anime_Title.Delete(titlesToDelete);
            RepoFactory.AniDB_Anime_Title.Save(titlesToSave);
        }

        private void CreateTags(List<Raw_AniDB_Tag> tags)
        {
            if (tags == null) return;

            this.AllTags = "";


            List<AniDB_Tag> tagsToSave = new List<AniDB_Tag>();
            List<AniDB_Anime_Tag> xrefsToSave = new List<AniDB_Anime_Tag>();
            List<AniDB_Anime_Tag> xrefsToDelete = new List<AniDB_Anime_Tag>();

            // find all the current links, and then later remove the ones that are no longer relevant
            List<AniDB_Anime_Tag> currentTags = RepoFactory.AniDB_Anime_Tag.GetByAnimeID(AnimeID);
            List<int> newTagIDs = new List<int>();

            foreach (Raw_AniDB_Tag rawtag in tags)
            {
                AniDB_Tag tag = RepoFactory.AniDB_Tag.GetByTagID(rawtag.TagID);
                if (tag == null) tag = new AniDB_Tag();

                if(!tag.Populate(rawtag)) continue;
                tagsToSave.Add(tag);

                newTagIDs.Add(tag.TagID);

                AniDB_Anime_Tag anime_tag =
                    RepoFactory.AniDB_Anime_Tag.GetByAnimeIDAndTagID(rawtag.AnimeID, rawtag.TagID);
                if (anime_tag == null) anime_tag = new AniDB_Anime_Tag();

                anime_tag.Populate(rawtag);
                xrefsToSave.Add(anime_tag);

                if (this.AllTags.Length > 0) this.AllTags += "|";
                this.AllTags += tag.TagName;
            }

            foreach (AniDB_Anime_Tag curTag in currentTags)
            {
                if (!newTagIDs.Contains(curTag.TagID))
                    xrefsToDelete.Add(curTag);
            }
            RepoFactory.AniDB_Tag.Save(tagsToSave);
            RepoFactory.AniDB_Anime_Tag.Save(xrefsToSave);
            RepoFactory.AniDB_Anime_Tag.Delete(xrefsToDelete);
        }

        private void CreateCharacters(ISession session, List<Raw_AniDB_Character> chars)
        {
            if (chars == null) return;


            ISessionWrapper sessionWrapper = session.Wrap();

            // delete all the existing cross references just in case one has been removed
            List<AniDB_Anime_Character> animeChars =
                RepoFactory.AniDB_Anime_Character.GetByAnimeID(sessionWrapper, AnimeID);

            RepoFactory.AniDB_Anime_Character.Delete(animeChars);


            List<AniDB_Character> chrsToSave = new List<AniDB_Character>();
            List<AniDB_Anime_Character> xrefsToSave = new List<AniDB_Anime_Character>();

            Dictionary<int, AniDB_Seiyuu> seiyuuToSave = new Dictionary<int, AniDB_Seiyuu>();
            List<AniDB_Character_Seiyuu> seiyuuXrefToSave = new List<AniDB_Character_Seiyuu>();

            // delete existing relationships to seiyuu's
            List<AniDB_Character_Seiyuu> charSeiyuusToDelete = new List<AniDB_Character_Seiyuu>();
            foreach (Raw_AniDB_Character rawchar in chars)
            {
                // delete existing relationships to seiyuu's
                List<AniDB_Character_Seiyuu> allCharSei =
                    RepoFactory.AniDB_Character_Seiyuu.GetByCharID(session, rawchar.CharID);
                foreach (AniDB_Character_Seiyuu xref in allCharSei)
                    charSeiyuusToDelete.Add(xref);
            }
            RepoFactory.AniDB_Character_Seiyuu.Delete(charSeiyuusToDelete);

            foreach (Raw_AniDB_Character rawchar in chars)
            {
                AniDB_Character chr = RepoFactory.AniDB_Character.GetByCharID(sessionWrapper, rawchar.CharID);
                if (chr == null)
                    chr = new AniDB_Character();

                if (!chr.PopulateFromHTTP(rawchar)) continue;
                chrsToSave.Add(chr);

                // create cross ref's between anime and character, but don't actually download anything
                AniDB_Anime_Character anime_char = new AniDB_Anime_Character();
                anime_char.Populate(rawchar);
                xrefsToSave.Add(anime_char);

                foreach (Raw_AniDB_Seiyuu rawSeiyuu in rawchar.Seiyuus)
                {
                    // save the link between character and seiyuu
                    AniDB_Character_Seiyuu acc = RepoFactory.AniDB_Character_Seiyuu.GetByCharIDAndSeiyuuID(session,
                        rawchar.CharID,
                        rawSeiyuu.SeiyuuID);
                    if (acc == null)
                    {
                        acc = new AniDB_Character_Seiyuu
                        {
                            CharID = chr.CharID,
                            SeiyuuID = rawSeiyuu.SeiyuuID
                        };
                        seiyuuXrefToSave.Add(acc);
                    }

                    // save the seiyuu
                    AniDB_Seiyuu seiyuu = RepoFactory.AniDB_Seiyuu.GetBySeiyuuID(session, rawSeiyuu.SeiyuuID);
                    if (seiyuu == null) seiyuu = new AniDB_Seiyuu();
                    seiyuu.PicName = rawSeiyuu.PicName;
                    seiyuu.SeiyuuID = rawSeiyuu.SeiyuuID;
                    seiyuu.SeiyuuName = rawSeiyuu.SeiyuuName;
                    seiyuuToSave[seiyuu.SeiyuuID] = seiyuu;
                }
            }
            RepoFactory.AniDB_Character.Save(chrsToSave);
            RepoFactory.AniDB_Anime_Character.Save(xrefsToSave);
            RepoFactory.AniDB_Seiyuu.Save(seiyuuToSave.Values.ToList());
            RepoFactory.AniDB_Character_Seiyuu.Save(seiyuuXrefToSave);
        }

        private void CreateRelations(ISession session, List<Raw_AniDB_RelatedAnime> rels, bool downloadRelations)
        {
            if (rels == null) return;


            List<AniDB_Anime_Relation> relsToSave = new List<AniDB_Anime_Relation>();
            List<CommandRequest_GetAnimeHTTP> cmdsToSave = new List<CommandRequest_GetAnimeHTTP>();

            foreach (Raw_AniDB_RelatedAnime rawrel in rels)
            {
                AniDB_Anime_Relation anime_rel = RepoFactory.AniDB_Anime_Relation.GetByAnimeIDAndRelationID(session,
                    rawrel.AnimeID,
                    rawrel.RelatedAnimeID);
                if (anime_rel == null) anime_rel = new AniDB_Anime_Relation();

                if (!anime_rel.Populate(rawrel)) continue;
                relsToSave.Add(anime_rel);

                if (downloadRelations || ServerSettings.AutoGroupSeries)
                {
                    logger.Info("Adding command to download related anime for {0} ({1}), related anime ID = {2}",
                        this.MainTitle, this.AnimeID, anime_rel.RelatedAnimeID);

                    // I have disable the downloading of relations here because of banning issues
                    // basically we will download immediate relations, but not relations of relations

                    //CommandRequest_GetAnimeHTTP cr_anime = new CommandRequest_GetAnimeHTTP(rawrel.RelatedAnimeID, false, downloadRelations);
                    CommandRequest_GetAnimeHTTP cr_anime = new CommandRequest_GetAnimeHTTP(anime_rel.RelatedAnimeID,
                        false, false);
                    cmdsToSave.Add(cr_anime);
                }
            }
            RepoFactory.AniDB_Anime_Relation.Save(relsToSave);

            // this is not part of the session/transaction because it does other operations in the save
            foreach (CommandRequest_GetAnimeHTTP cmd in cmdsToSave)
                cmd.Save();
        }

        private void CreateSimilarAnime(ISession session, List<Raw_AniDB_SimilarAnime> sims)
        {
            if (sims == null) return;


            List<AniDB_Anime_Similar> recsToSave = new List<AniDB_Anime_Similar>();

            foreach (Raw_AniDB_SimilarAnime rawsim in sims)
            {
                AniDB_Anime_Similar anime_sim = RepoFactory.AniDB_Anime_Similar.GetByAnimeIDAndSimilarID(session,
                    rawsim.AnimeID,
                    rawsim.SimilarAnimeID);
                if (anime_sim == null) anime_sim = new AniDB_Anime_Similar();

                anime_sim.Populate(rawsim);
                recsToSave.Add(anime_sim);
            }
            RepoFactory.AniDB_Anime_Similar.Save(recsToSave);
        }

        private void CreateRecommendations(ISession session, List<Raw_AniDB_Recommendation> recs)
        {
            if (recs == null) return;

            //AniDB_RecommendationRepository repRecs = new AniDB_RecommendationRepository();

            List<AniDB_Recommendation> recsToSave = new List<AniDB_Recommendation>();
            foreach (Raw_AniDB_Recommendation rawRec in recs)
            {
                AniDB_Recommendation rec =
                    RepoFactory.AniDB_Recommendation.GetByAnimeIDAndUserID(session, rawRec.AnimeID, rawRec.UserID);
                if (rec == null)
                    rec = new AniDB_Recommendation();
                rec.Populate(rawRec);
                recsToSave.Add(rec);
            }
            RepoFactory.AniDB_Recommendation.Save(recsToSave);
        }

        private void CreateAnimeReviews()
        {
            if (reviewIDListRAW != null)
                //Only create relations if the origin of the data if from Raw (WebService/AniDB)
            {
                if (reviewIDListRAW.Trim().Length == 0)
                    return;

                //Delete old if changed
                List<AniDB_Anime_Review> animeReviews = RepoFactory.AniDB_Anime_Review.GetByAnimeID(AnimeID);
                foreach (AniDB_Anime_Review xref in animeReviews)
                {
                    RepoFactory.AniDB_Anime_Review.Delete(xref.AniDB_Anime_ReviewID);
                }


                string[] revs = reviewIDListRAW.Split(',');
                foreach (string review in revs)
                {
                    if (review.Trim().Length > 0)
                    {
                        Int32.TryParse(review.Trim(), out int rev);
                        if (rev != 0)
                        {
                            AniDB_Anime_Review csr = new AniDB_Anime_Review
                            {
                                AnimeID = this.AnimeID,
                                ReviewID = rev
                            };
                            RepoFactory.AniDB_Anime_Review.Save(csr);
                        }
                    }
                }
            }
        }


        private CL_AniDB_Anime GenerateContract(ISessionWrapper session, List<AniDB_Anime_Title> titles)
        {
            List<CL_AniDB_Character> characters = GetCharactersContract();
            List<MovieDB_Fanart> movDbFanart = null;
            List<TvDB_ImageFanart> tvDbFanart = null;
            List<TvDB_ImageWideBanner> tvDbBanners = null;

            if (this.GetAnimeTypeEnum() == Shoko.Models.Enums.AnimeType.Movie)
            {
                movDbFanart = GetMovieDBFanarts(session);
            }
            else
            {
                tvDbFanart = GetTvDBImageFanarts();
                tvDbBanners = GetTvDBImageWideBanners();
            }

            CL_AniDB_Anime cl = GenerateContract(titles, null, characters, movDbFanart, tvDbFanart, tvDbBanners);
            AniDB_Anime_DefaultImage defFanart = GetDefaultFanart(session);
            AniDB_Anime_DefaultImage defPoster = GetDefaultPoster(session);
            AniDB_Anime_DefaultImage defBanner = GetDefaultWideBanner(session);

            cl.DefaultImageFanart = defFanart?.ToClient(session);
            cl.DefaultImagePoster = defPoster?.ToClient(session);
            cl.DefaultImageWideBanner = defBanner?.ToClient(session);

            return cl;
        }

        private CL_AniDB_Anime GenerateContract(List<AniDB_Anime_Title> titles, DefaultAnimeImages defaultImages,
            List<CL_AniDB_Character> characters, IEnumerable<MovieDB_Fanart> movDbFanart,
            IEnumerable<TvDB_ImageFanart> tvDbFanart,
            IEnumerable<TvDB_ImageWideBanner> tvDbBanners)
        {
            CL_AniDB_Anime cl = this.ToClient();
            cl.FormattedTitle = GetFormattedTitle(titles);
            cl.Characters = characters;

            if (defaultImages != null)
            {
                cl.DefaultImageFanart = defaultImages.Fanart?.ToContract();
                cl.DefaultImagePoster = defaultImages.Poster?.ToContract();
                cl.DefaultImageWideBanner = defaultImages.WideBanner?.ToContract();
            }

            if (this.GetAnimeTypeEnum() == Shoko.Models.Enums.AnimeType.Movie)
            {
                cl.Fanarts = movDbFanart?.Select(a => new CL_AniDB_Anime_DefaultImage
                    {
                        ImageType = (int) JMMImageType.MovieDB_FanArt,
                        MovieFanart = a,
                        AniDB_Anime_DefaultImageID = a.MovieDB_FanartID
                    })
                    .ToList();
            }
            else // Not a movie
            {
                cl.Fanarts = tvDbFanart?.Select(a => new CL_AniDB_Anime_DefaultImage
                    {
                        ImageType = (int) JMMImageType.TvDB_FanArt,
                        TVFanart = a,
                        AniDB_Anime_DefaultImageID = a.TvDB_ImageFanartID
                    })
                    .ToList();
                cl.Banners = tvDbBanners?.Select(a => new CL_AniDB_Anime_DefaultImage
                    {
                        ImageType = (int) JMMImageType.TvDB_Banner,
                        TVWideBanner = a,
                        AniDB_Anime_DefaultImageID = a.TvDB_ImageWideBannerID
                    })
                    .ToList();
            }

            if (cl.Fanarts?.Count == 0) cl.Fanarts = null;
            if (cl.Banners?.Count == 0) cl.Banners = null;

            return cl;
        }

        public List<CL_AniDB_Character> GetCharactersContract()
        {
            List<CL_AniDB_Character> chars = new List<CL_AniDB_Character>();

            try
            {
                List<AniDB_Anime_Character> animeChars = RepoFactory.AniDB_Anime_Character.GetByAnimeID(this.AnimeID);
                if (animeChars == null || animeChars.Count == 0) return chars;

                foreach (AniDB_Anime_Character animeChar in animeChars)
                {
                    AniDB_Character chr = RepoFactory.AniDB_Character.GetByCharID(animeChar.CharID);
                    if (chr != null)
                        chars.Add(chr.ToClient(animeChar.CharType));
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
            return chars;
        }

        public static void UpdateContractDetailedBatch(ISessionWrapper session,
            IReadOnlyCollection<SVR_AniDB_Anime> animeColl)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));
            if (animeColl == null)
                throw new ArgumentNullException(nameof(animeColl));

            int[] animeIds = animeColl.Select(a => a.AnimeID).ToArray();

            var titlesByAnime = RepoFactory.AniDB_Anime_Title.GetByAnimeIDs(session, animeIds);
            var animeTagsByAnime = RepoFactory.AniDB_Anime_Tag.GetByAnimeIDs(session, animeIds);
            var tagsByAnime = RepoFactory.AniDB_Tag.GetByAnimeIDs(session, animeIds);
            var custTagsByAnime = RepoFactory.CustomTag.GetByAnimeIDs(session, animeIds);
            var voteByAnime = RepoFactory.AniDB_Vote.GetByAnimeIDs(session, animeIds);
            var audioLangByAnime = RepoFactory.Adhoc.GetAudioLanguageStatsByAnime(session, animeIds);
            var subtitleLangByAnime = RepoFactory.Adhoc.GetSubtitleLanguageStatsByAnime(session, animeIds);
            var vidQualByAnime = RepoFactory.Adhoc.GetAllVideoQualityByAnime(session, animeIds);
            var epVidQualByAnime = RepoFactory.Adhoc.GetEpisodeVideoQualityStatsByAnime(session, animeIds);
            var defImagesByAnime = RepoFactory.AniDB_Anime.GetDefaultImagesByAnime(session, animeIds);
            var charsByAnime = RepoFactory.AniDB_Character.GetCharacterAndSeiyuuByAnime(session, animeIds);
            var movDbFanartByAnime = RepoFactory.MovieDB_Fanart.GetByAnimeIDs(session, animeIds);
            var tvDbBannersByAnime = RepoFactory.TvDB_ImageWideBanner.GetByAnimeIDs(session, animeIds);
            var tvDbFanartByAnime = RepoFactory.TvDB_ImageFanart.GetByAnimeIDs(session, animeIds);

            foreach (SVR_AniDB_Anime anime in animeColl)
            {
                var contract = new CL_AniDB_AnimeDetailed();
                var animeTitles = titlesByAnime[anime.AnimeID];

                defImagesByAnime.TryGetValue(anime.AnimeID, out DefaultAnimeImages defImages);

                var characterContracts = (charsByAnime[anime.AnimeID] ?? Enumerable.Empty<AnimeCharacterAndSeiyuu>())
                    .Select(ac => ac.ToClient())
                    .ToList();
                var movieDbFanart = movDbFanartByAnime[anime.AnimeID];
                var tvDbBanners = tvDbBannersByAnime[anime.AnimeID];
                var tvDbFanart = tvDbFanartByAnime[anime.AnimeID];

                contract.AniDBAnime = anime.GenerateContract(animeTitles.ToList(), defImages, characterContracts,
                    movieDbFanart, tvDbFanart, tvDbBanners);

                // Anime titles
                contract.AnimeTitles = titlesByAnime[anime.AnimeID]
                    .Select(t => new CL_AnimeTitle
                    {
                        AnimeID = t.AnimeID,
                        Language = t.Language,
                        Title = t.Title,
                        TitleType = t.TitleType
                    })
                    .ToList();

                // Seasons
                if (anime.AirDate != null)
                {
                    int beginYear = anime.AirDate.Value.Year;
                    int endYear = anime.EndDate?.Year ?? DateTime.Today.Year;
                    for (int year = beginYear; year <= endYear; year++)
                    {
                        foreach (AnimeSeason season in Enum.GetValues(typeof(AnimeSeason)))
                        {
                            if (anime.IsInSeason(season, year)) contract.Stat_AllSeasons.Add($"{season} {year}");
                        }
                    }
                }

                // Anime tags
                var dictAnimeTags = animeTagsByAnime[anime.AnimeID]
                    .ToDictionary(t => t.TagID);

                contract.Tags = tagsByAnime[anime.AnimeID]
                    .Select(t =>
                    {
                        CL_AnimeTag ctag = new CL_AnimeTag
                        {
                            GlobalSpoiler = t.GlobalSpoiler,
                            LocalSpoiler = t.LocalSpoiler,
                            TagDescription = t.TagDescription,
                            TagID = t.TagID,
                            TagName = t.TagName,
                            Weight = dictAnimeTags.TryGetValue(t.TagID, out AniDB_Anime_Tag animeTag) ? animeTag.Weight : 0
                        };

                        return ctag;
                    })
                    .ToList();

                // Custom tags
                contract.CustomTags = custTagsByAnime[anime.AnimeID];

                // Vote

                if (voteByAnime.TryGetValue(anime.AnimeID, out AniDB_Vote vote))
                {
                    contract.UserVote = vote;
                }


                // Subtitle languages
                contract.Stat_AudioLanguages = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

                if (audioLangByAnime.TryGetValue(anime.AnimeID, out LanguageStat langStat))
                {
                    contract.Stat_AudioLanguages.UnionWith(langStat.LanguageNames);
                }

                // Audio languages
                contract.Stat_SubtitleLanguages = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

                if (subtitleLangByAnime.TryGetValue(anime.AnimeID, out langStat))
                {
                    contract.Stat_SubtitleLanguages.UnionWith(langStat.LanguageNames);
                }

                // Anime video quality

                contract.Stat_AllVideoQuality = vidQualByAnime.TryGetValue(anime.AnimeID, out HashSet<string> vidQual)
                    ? vidQual
                    : new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

                // Episode video quality

                contract.Stat_AllVideoQuality_Episodes = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

                if (epVidQualByAnime.TryGetValue(anime.AnimeID, out AnimeVideoQualityStat vidQualStat) &&
                    vidQualStat.VideoQualityEpisodeCount.Count > 0)
                {
                    contract.Stat_AllVideoQuality_Episodes.UnionWith(vidQualStat.VideoQualityEpisodeCount
                        .Where(kvp => kvp.Value >= anime.EpisodeCountNormal)
                        .Select(kvp => kvp.Key));
                }

                anime.Contract = contract;
            }
        }

        public void UpdateContractDetailed(ISessionWrapper session)
        {
            List<AniDB_Anime_Title> animeTitles = RepoFactory.AniDB_Anime_Title.GetByAnimeID(AnimeID);
            CL_AniDB_AnimeDetailed cl = new CL_AniDB_AnimeDetailed
            {
                AniDBAnime = GenerateContract(session, animeTitles),


                AnimeTitles = new List<CL_AnimeTitle>(),
                Tags = new List<CL_AnimeTag>(),
                CustomTags = new List<CustomTag>()
            };

            // get all the anime titles
            if (animeTitles != null)
            {
                foreach (AniDB_Anime_Title title in animeTitles)
                {
                    CL_AnimeTitle ctitle = new CL_AnimeTitle
                    {
                        AnimeID = title.AnimeID,
                        Language = title.Language,
                        Title = title.Title,
                        TitleType = title.TitleType
                    };
                    cl.AnimeTitles.Add(ctitle);
                }
            }

            if (AirDate != null)
            {
                int beginYear = AirDate.Value.Year;
                int endYear = EndDate?.Year ?? DateTime.Today.Year;
                for (int year = beginYear; year <= endYear; year++)
                {
                    foreach (AnimeSeason season in Enum.GetValues(typeof(AnimeSeason)))
                    {
                        if (this.IsInSeason(season, year)) cl.Stat_AllSeasons.Add($"{season} {year}");
                    }
                }
            }

            Dictionary<int, AniDB_Anime_Tag> dictAnimeTags = new Dictionary<int, AniDB_Anime_Tag>();
            foreach (AniDB_Anime_Tag animeTag in GetAnimeTags())
                dictAnimeTags[animeTag.TagID] = animeTag;

            foreach (AniDB_Tag tag in GetAniDBTags())
            {
                CL_AnimeTag ctag = new CL_AnimeTag
                {
                    GlobalSpoiler = tag.GlobalSpoiler,
                    LocalSpoiler = tag.LocalSpoiler,
                    //ctag.Spoiler = tag.Spoiler;
                    //ctag.TagCount = tag.TagCount;
                    TagDescription = tag.TagDescription,
                    TagID = tag.TagID,
                    TagName = tag.TagName
                };
                if (dictAnimeTags.ContainsKey(tag.TagID))
                    ctag.Weight = dictAnimeTags[tag.TagID].Weight;
                else
                    ctag.Weight = 0;

                cl.Tags.Add(ctag);
            }


            // Get all the custom tags
            foreach (CustomTag custag in GetCustomTagsForAnime())
                cl.CustomTags.Add(custag);

            if (this.UserVote != null)
                cl.UserVote = this.UserVote;

            HashSet<string> audioLanguages = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            HashSet<string> subtitleLanguages = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            //logger.Trace(" XXXX 06");

            // audio languages
            Dictionary<int, LanguageStat> dicAudio =
                RepoFactory.Adhoc.GetAudioLanguageStatsByAnime(session, this.AnimeID);
            foreach (KeyValuePair<int, LanguageStat> kvp in dicAudio)
            {
                foreach (string lanName in kvp.Value.LanguageNames)
                {
                    if (!audioLanguages.Contains(lanName))
                        audioLanguages.Add(lanName);
                }
            }

            //logger.Trace(" XXXX 07");

            // subtitle languages
            Dictionary<int, LanguageStat> dicSubtitle =
                RepoFactory.Adhoc.GetSubtitleLanguageStatsByAnime(session, this.AnimeID);
            foreach (KeyValuePair<int, LanguageStat> kvp in dicSubtitle)
            {
                foreach (string lanName in kvp.Value.LanguageNames)
                {
                    if (!subtitleLanguages.Contains(lanName))
                        subtitleLanguages.Add(lanName);
                }
            }

            //logger.Trace(" XXXX 08");

            cl.Stat_AudioLanguages = audioLanguages;

            //logger.Trace(" XXXX 09");

            cl.Stat_SubtitleLanguages = subtitleLanguages;

            //logger.Trace(" XXXX 10");
            cl.Stat_AllVideoQuality = RepoFactory.Adhoc.GetAllVideoQualityForAnime(session, this.AnimeID);

            AnimeVideoQualityStat stat = RepoFactory.Adhoc.GetEpisodeVideoQualityStatsForAnime(session, this.AnimeID);
            cl.Stat_AllVideoQuality_Episodes = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            if (stat != null && stat.VideoQualityEpisodeCount.Count > 0)
            {
                foreach (KeyValuePair<string, int> kvp in stat.VideoQualityEpisodeCount)
                {
                    if (kvp.Value >= EpisodeCountNormal)
                    {
                        cl.Stat_AllVideoQuality_Episodes.Add(kvp.Key);
                    }
                }
            }

            //logger.Trace(" XXXX 11");

            Contract = cl;
        }


        public Azure_AnimeFull ToAzure()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return ToAzure(session.Wrap());
            }
        }

        public Azure_AnimeFull ToAzure(ISessionWrapper session)
        {
            Azure_AnimeFull contract = new Azure_AnimeFull
            {
                Detail = new Azure_AnimeDetail(),
                Characters = new List<Azure_AnimeCharacter>(),
                Comments = new List<Azure_AnimeComment>()
            };
            contract.Detail.AllTags = this.TagsString;
            contract.Detail.AllCategories = this.TagsString;
            contract.Detail.AnimeID = this.AnimeID;
            contract.Detail.AnimeName = this.MainTitle;
            contract.Detail.AnimeType = this.GetAnimeTypeDescription();
            contract.Detail.Description = this.Description;
            contract.Detail.EndDateLong = AniDB.GetAniDBDateAsSeconds(this.EndDate);
            contract.Detail.StartDateLong = AniDB.GetAniDBDateAsSeconds(this.AirDate);
            contract.Detail.EpisodeCountNormal = this.EpisodeCountNormal;
            contract.Detail.EpisodeCountSpecial = this.EpisodeCountSpecial;
            contract.Detail.FanartURL = GetDefaultFanartOnlineURL(session);
            contract.Detail.OverallRating = this.GetAniDBRating();
            contract.Detail.PosterURL = String.Format(Constants.URLS.AniDB_Images, Picname);
            contract.Detail.TotalVotes = this.GetAniDBTotalVotes();


            List<AniDB_Anime_Character> animeChars = RepoFactory.AniDB_Anime_Character.GetByAnimeID(session, AnimeID);

            if (animeChars != null || animeChars.Count > 0)
            {
                // first get all the main characters
                foreach (
                    AniDB_Anime_Character animeChar in
                    animeChars.Where(
                        item =>
                            item.CharType.Equals("main character in", StringComparison.InvariantCultureIgnoreCase)))
                {
                    AniDB_Character chr = RepoFactory.AniDB_Character.GetByCharID(session, animeChar.CharID);
                    if (chr != null)
                        contract.Characters.Add(chr.ToContractAzure(animeChar));
                }

                // now get the rest
                foreach (
                    AniDB_Anime_Character animeChar in
                    animeChars.Where(
                        item =>
                            !item.CharType.Equals("main character in", StringComparison.InvariantCultureIgnoreCase))
                )
                {
                    AniDB_Character chr = RepoFactory.AniDB_Character.GetByCharID(session, animeChar.CharID);
                    if (chr != null)
                        contract.Characters.Add(chr.ToContractAzure(animeChar));
                }
            }


            foreach (AniDB_Recommendation rec in RepoFactory.AniDB_Recommendation.GetByAnimeID(session, AnimeID))
            {
                Azure_AnimeComment comment = new Azure_AnimeComment
                {
                    UserID = rec.UserID,
                    UserName = "",

                    // Comment details
                    CommentText = rec.RecommendationText,
                    IsSpoiler = false,
                    CommentDateLong = 0,

                    ImageURL = String.Empty
                };
                AniDBRecommendationType recType = (AniDBRecommendationType) rec.RecommendationType;
                switch (recType)
                {
                    case AniDBRecommendationType.ForFans:
                        comment.CommentType = (int) WhatPeopleAreSayingType.AniDBForFans;
                        break;
                    case AniDBRecommendationType.MustSee:
                        comment.CommentType = (int) WhatPeopleAreSayingType.AniDBMustSee;
                        break;
                    case AniDBRecommendationType.Recommended:
                        comment.CommentType = (int) WhatPeopleAreSayingType.AniDBRecommendation;
                        break;
                }

                comment.Source = "AniDB";
                contract.Comments.Add(comment);
            }

            return contract;
        }

        public SVR_AnimeSeries CreateAnimeSeriesAndGroup()
        {
            using (var session = DatabaseFactory.SessionFactory.OpenSession())
            {
                return CreateAnimeSeriesAndGroup(session.Wrap());
            }
        }

        public SVR_AnimeSeries CreateAnimeSeriesAndGroup(ISessionWrapper session)
        {
            // Create a new AnimeSeries record
            SVR_AnimeSeries series = new SVR_AnimeSeries();

            series.Populate(this);

            SVR_AnimeGroup grp = new AnimeGroupCreator().GetOrCreateSingleGroupForSeries(session, series);

            series.AnimeGroupID = grp.AnimeGroupID;
            RepoFactory.AnimeSeries.Save(series, false, false);

            // check for TvDB associations
            if (this.Restricted == 0)
            {
                CommandRequest_TvDBSearchAnime cmd = new CommandRequest_TvDBSearchAnime(AnimeID, forced: false);
                cmd.Save();

                // check for Trakt associations
                if (ServerSettings.Trakt_IsEnabled && !String.IsNullOrEmpty(ServerSettings.Trakt_AuthToken))
                {
                    CommandRequest_TraktSearchAnime cmd2 = new CommandRequest_TraktSearchAnime(AnimeID, forced: false);
                    cmd2.Save();
                }

                if (AnimeType == (int) Shoko.Models.Enums.AnimeType.Movie)
                {
                    CommandRequest_MovieDBSearchAnime cmd3 =
                        new CommandRequest_MovieDBSearchAnime(AnimeID, false);
                    cmd3.Save();
                }
            }

            return series;
        }

        public static void GetRelatedAnimeRecursive(ISessionWrapper session, int animeID,
            ref List<SVR_AniDB_Anime> relList,
            ref List<int> relListIDs, ref List<int> searchedIDs)
        {
            SVR_AniDB_Anime anime = RepoFactory.AniDB_Anime.GetByAnimeID(animeID);
            searchedIDs.Add(animeID);

            foreach (AniDB_Anime_Relation rel in anime.GetRelatedAnime(session))
            {
                string relationtype = rel.RelationType.ToLower();
                if (SVR_AnimeGroup.IsRelationTypeInExclusions(relationtype))
                {
                    //Filter these relations these will fix messes, like Gundam , Clamp, etc.
                    continue;
                }
                SVR_AniDB_Anime relAnime = RepoFactory.AniDB_Anime.GetByAnimeID(session, rel.RelatedAnimeID);
                if (relAnime != null && !relListIDs.Contains(relAnime.AnimeID))
                {
                    if (SVR_AnimeGroup.IsRelationTypeInExclusions(relAnime.GetAnimeTypeDescription().ToLower()))
                        continue;
                    relList.Add(relAnime);
                    relListIDs.Add(relAnime.AnimeID);
                    if (!searchedIDs.Contains(rel.RelatedAnimeID))
                    {
                        GetRelatedAnimeRecursive(session, rel.RelatedAnimeID, ref relList, ref relListIDs,
                            ref searchedIDs);
                    }
                }
            }
        }

        public static void UpdateStatsByAnimeID(int id)
        {
            SVR_AniDB_Anime an = RepoFactory.AniDB_Anime.GetByAnimeID(id);
            if (an != null)
                RepoFactory.AniDB_Anime.Save(an);
            SVR_AnimeSeries series = RepoFactory.AnimeSeries.GetByAnimeID(id);
            if (series != null)
            {
                // Update more than just stats in case the xrefs have changed
                series.UpdateStats(true, true, false);
                RepoFactory.AnimeSeries.Save(series, true, false);
            }
        }
    }
}