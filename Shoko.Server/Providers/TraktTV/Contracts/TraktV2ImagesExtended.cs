﻿using System.Runtime.Serialization;

namespace Shoko.Server.Providers.TraktTV.Contracts
{
    [DataContract(Name = "images")]
    public class TraktV2ImagesExtended
    {
        [DataMember(Name = "fanart")]
        public TraktV2Fanart fanart { get; set; }

        [DataMember(Name = "poster")]
        public TraktV2Poster poster { get; set; }

        [DataMember(Name = "logo")]
        public TraktV2Logo logo { get; set; }

        [DataMember(Name = "clearart")]
        public TraktV2Clearart clearart { get; set; }

        [DataMember(Name = "banner")]
        public TraktV2Banner banner { get; set; }

        [DataMember(Name = "thumb")]
        public TraktV2Thumb thumb { get; set; }
    }
}