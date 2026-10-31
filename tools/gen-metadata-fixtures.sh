#!/usr/bin/env bash
# Generates small tagged MP4-family and Matroska files for the tag and fact comparison, from synthetic sources, with ffmpeg, MP4Box,
# mkvmerge and ExifTool as black-box writers. The people, places and devices in the tags are invented.
# Usage: OUT=<dir> [ORACLES=<dir with ffmpeg, MP4Box, mkvmerge, exiftool>] tools/gen-metadata-fixtures.sh
# The writers' versions are recorded in $OUT/VERSIONS.txt.
set -u
OUT=${OUT:?set OUT to the output directory}
ORACLES=${ORACLES:-$HOME/onemedia/oracles/bin}
FF=$ORACLES/ffmpeg
MP4BOX=$ORACLES/MP4Box
MKVMERGE=$ORACLES/mkvmerge
ET=$ORACLES/exiftool
mkdir -p "$OUT"
cd "$OUT"
{ "$FF" -version | head -1; "$MP4BOX" -version 2>&1 | head -1; "$MKVMERGE" --version; "$ET" -ver; } > VERSIONS.txt 2>&1

Q="-hide_banner -loglevel error -y"
V="-f lavfi -i testsrc2=size=160x120:rate=25:duration=1"
A="-f lavfi -i sine=frequency=440:sample_rate=44100:duration=1"
ENC="-c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -b:a 48k"
g() { local name=$1; shift; "$FF" $Q "$@" "$name" || echo "FAILED: $name" >&2; }

"$FF" $Q -f lavfi -i testsrc2=size=64x64:rate=1 -frames:v 1 cover.png
"$FF" $Q -f lavfi -i testsrc2=size=64x64:rate=1 -frames:v 1 -q:v 5 cover.jpg

ALL="-metadata title=Harbour_Lights -metadata artist=Ada_Quill -metadata album=Night_Ferries -metadata album_artist=Ada_Quill -metadata composer=Basil_Ferne -metadata genre=Ambient -metadata comment=First_take -metadata description=A_short_description -metadata date=2023-04-05 -metadata copyright=2023_Example_Records -metadata track=3/12 -metadata disc=1/2 -metadata lyrics=La_la -metadata encoded_by=Example_Studio -metadata grouping=Set_A"
UNI="-metadata title=Beyoncé_—_ΩΣ_日本語 -metadata artist=Zoë_Müller"

# ISO base media: iTunes items (mp4), QuickTime user data (mov), key-table metadata, 3GPP boxes.
g tags_ilst.mp4 $V $A $ENC $ALL
g tags_ilst_unicode.mp4 $V $A $ENC $UNI
g tags_ilst.m4a -f lavfi -i sine=frequency=440:sample_rate=44100:duration=1 -c:a aac -b:a 48k -f ipod $ALL
g tags_udta.mov $V $A $ENC $ALL -metadata location=+37.7749-122.4194+012.500/ -metadata location-eng=+37.7749-122.4194+012.500/
g tags_udta_year.mov $V $ENC -metadata title=Year_Only -metadata date=2021
g tags_keys.mov $V $A $ENC -movflags use_metadata_tags -metadata com.apple.quicktime.title=Keyed_Title -metadata com.apple.quicktime.make=Contoso -metadata com.apple.quicktime.model=Camera_X1 -metadata com.apple.quicktime.software=17.1 -metadata com.apple.quicktime.creationdate=2023-04-05T10:11:12+0200 -metadata com.apple.quicktime.location.ISO6709=+37.7749-122.4194+012.500/ -metadata com.apple.quicktime.author=Pat_Doe -metadata com.apple.quicktime.comment=Keyed_comment
g tags_keys.mp4 $V $A $ENC -movflags use_metadata_tags -metadata com.apple.quicktime.make=Fabrikam -metadata com.apple.quicktime.model=Phone_9 -metadata com.apple.quicktime.creationdate=2022-12-31T23:59:58-0800 -metadata com.apple.quicktime.location.ISO6709=-33.8688+151.2093/
g tags_keys_android.mp4 $V $A $ENC -movflags use_metadata_tags -metadata com.android.manufacturer=Northwind -metadata com.android.model=Droid_7 -metadata com.android.version=14 -metadata creation_time=2023-06-01T09:30:00Z
g tags_creation_time.mp4 $V $A $ENC -metadata creation_time=2023-04-05T08:11:12Z
g tags_location.mp4 $V $A $ENC -metadata location=+48.8584+002.2945/ -metadata title=Location_only
g tags_gps_dms.mov $V $ENC -movflags use_metadata_tags -metadata com.apple.quicktime.location.ISO6709=+374629.4-1221625.2/ -metadata com.apple.quicktime.make=Tailspin
g tags_gps_ddm.mov $V $ENC -movflags use_metadata_tags -metadata com.apple.quicktime.location.ISO6709=+3746.49-12225.16+5.0/
g tags_bad_gps.mov $V $ENC -movflags use_metadata_tags -metadata com.apple.quicktime.location.ISO6709=north_of_here
g tags_rot90.mp4 -display_rotation 90 $V $ENC -metadata title=Rotated
g tags_rot270.mov -display_rotation 270 $V $ENC -metadata title=Rotated270
g tags_3gp.3gp -f lavfi -i testsrc2=size=176x144:rate=15:duration=1 -f lavfi -i sine=frequency=440:sample_rate=8000:duration=1 -c:v h263 -c:a aac -ac 1 -b:a 24k -metadata title=Pocket_Clip -metadata author=Lee_Hart -metadata artist=Lee_Hart -metadata album=Handset_Reel -metadata year=2019 -metadata genre=Demo -metadata description=Sample_3gp -metadata copyright=2019_Lee -metadata location=+51.5007-000.1246/
g tags_3gp_rot.3gp -f lavfi -i testsrc2=size=176x144:rate=15:duration=1 -c:v h263 -an -metadata title=Silent_Clip
g tags_cover.mp4 $V $A -i cover.png -map 0:v -map 1:a -map 2:v -c:v:0 libx264 -pix_fmt yuv420p -c:a aac -b:a 48k -c:v:1 copy -disposition:v:1 attached_pic -metadata title=With_Cover
g tags_cover.m4a -f lavfi -i sine=frequency=440:sample_rate=44100:duration=1 -i cover.jpg -map 0:a -map 1:v -c:a aac -b:a 48k -c:v copy -disposition:v:0 attached_pic -f ipod -metadata title=Cover_Song -metadata artist=Ada_Quill
printf ';FFMETADATA1\ntitle=Chaptered\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=400\ntitle=Intro\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=400\nEND=1000\ntitle=Main\n' > chapters.ffmeta
g tags_chapters.mp4 $V $A -i chapters.ffmeta -map 0 -map_metadata 2 $ENC
g tags_chapters.mov $V $A -i chapters.ffmeta -map 0 -map_metadata 2 $ENC
g plain_notags.mp4 $V $A $ENC -map_metadata -1 -fflags +bitexact -flags:v +bitexact -flags:a +bitexact
g frag_tags.mp4 $V $A $ENC -movflags frag_keyframe+empty_moov+default_base_moof -metadata title=Fragmented -metadata artist=Ada_Quill -metadata creation_time=2022-01-02T03:04:05Z
g faststart_tags.mp4 $V $A $ENC -movflags +faststart $ALL
g long_comment.mp4 $V $ENC -metadata comment="$(head -c 70000 /dev/zero | tr '\0' 'x')"
g multi_artist.mov $V $ENC -metadata artist=One -metadata ARTIST=Two -metadata title=Dup

# MP4Box iTunes tags and cover art.
cp tags_ilst.mp4 mp4box_itags.mp4
"$MP4BOX" -itags "name=Boxed_Title:artist=Boxed_Artist:album=Boxed_Album:genre=Jazz:created=2020-02-03:comment=Boxed_comment:track=2/9:disk=1/1:cover=cover.png:tool=Box_Tool" mp4box_itags.mp4 >/dev/null 2>&1 || echo "FAILED: mp4box_itags" >&2
cp plain_notags.mp4 mp4box_itags_empty.mp4
"$MP4BOX" -itags "name=Only_Name" mp4box_itags_empty.mp4 >/dev/null 2>&1 || echo "FAILED: mp4box_itags_empty" >&2

# ExifTool as a black-box writer: XMP packets, Keys, UserData and ItemList tags.
cp plain_notags.mp4 et_xmp.mp4
"$ET" -overwrite_original -q -XMP-xmp:CreateDate="2021-07-08 09:10:11+05:30" -XMP-tiff:Make=Litware -XMP-tiff:Model=Cam_Pro -XMP-xmp:CreatorTool=Edit_Suite_3 -XMP-dc:Title=XMP_Title et_xmp.mp4 || echo "FAILED: et_xmp" >&2
cp plain_notags.mp4 et_xmp_gps.mp4
"$ET" -overwrite_original -q -XMP-exif:GPSLatitude="37,46.494N" -XMP-exif:GPSLongitude="122,25.164W" -XMP-xmp:CreateDate="2020-01-02T03:04:05Z" et_xmp_gps.mp4 || echo "FAILED: et_xmp_gps" >&2
cp tags_keys.mov et_keys_added.mov
"$ET" -overwrite_original -q -Keys:Make=Adventure_Works -Keys:CreationDate="2019-09-09 09:09:09+01:00" -ItemList:Title=ET_Title -UserData:Copyright=ET_Copyright et_keys_added.mov || echo "FAILED: et_keys_added" >&2
cp plain_notags.mp4 et_userdata.mp4
"$ET" -overwrite_original -q -UserData:Make=UD_Make -UserData:Model=UD_Model -UserData:Software=UD_Soft -UserData:DateTimeOriginal="2018-05-06 07:08:09" -ItemList:Comment=ET_comment -ItemList:Artist=ET_Artist et_userdata.mp4 || echo "FAILED: et_userdata" >&2

# Matroska and WebM: ffmpeg, then mkvmerge with tag files.
g mkv_ffmpeg_tags.mkv $V $A $ENC $ALL -metadata creation_time=2022-03-04T05:06:07Z -metadata:s:a:0 title=Main_Audio -metadata:s:a:0 language=fra -metadata:s:v:0 title=Main_Video
g mkv_ffmpeg_unicode.mkv $V $ENC $UNI
g mkv_ffmpeg_cover.mkv $V $A $ENC -attach cover.jpg -metadata:s:t mimetype=image/jpeg -metadata title=With_Attachment
g mkv_ffmpeg_cover_png.mkv $V $ENC -attach cover.png -metadata:s:t mimetype=image/png -metadata:s:t filename=cover.png
g mkv_ffmpeg_chapters.mkv $V $A -i chapters.ffmeta -map 0 -map_metadata 2 $ENC
g mkv_ffmpeg_notags.mkv $V $A $ENC -map_metadata -1
g webm_tags.webm $V -c:v libvpx -b:v 200k -an -metadata title=WebM_Title -metadata artist=Ada_Quill -metadata comment=Web_comment
g webm_vp9_audio.webm $V $A -c:v libvpx-vp9 -b:v 200k -c:a libopus -b:a 32k -metadata title=WebM_Opus -metadata date=2020
g mka_tags.mka -f lavfi -i sine=frequency=440:sample_rate=44100:duration=1 -c:a libvorbis -metadata title=Track_Song -metadata artist=Ada_Quill -metadata album=Night_Ferries -metadata genre=Ambient -metadata track=3 -metadata date=2023
g mkv_ffmpeg_rot.mkv $V $ENC -metadata title=Rotated_mkv

cat > tags_global.xml <<'XML'
<?xml version="1.0" encoding="UTF-8"?>
<Tags>
  <Tag>
    <Targets><TargetTypeValue>50</TargetTypeValue><TargetType>MOVIE</TargetType></Targets>
    <Simple><Name>TITLE</Name><String>Boxed_Movie</String>
      <Simple><Name>SORT_WITH</Name><String>Movie_Boxed</String></Simple>
    </Simple>
    <Simple><Name>DIRECTOR</Name><String>Ida_Rowe</String></Simple>
    <Simple><Name>DATE_RELEASED</Name><String>2015</String></Simple>
    <Simple><Name>DESCRIPTION</Name><String>Described</String><TagLanguage>fra</TagLanguage></Simple>
    <Simple><Name>COMMENT</Name><String>Bcp47_comment</String><TagLanguageIETF>pt-BR</TagLanguageIETF></Simple>
    <Simple><Name>PLAY_COUNTER</Name><Binary>AAAAAQ==</Binary></Simple>
  </Tag>
  <Tag>
    <Targets><TargetTypeValue>30</TargetTypeValue><TargetType>TRACK</TargetType></Targets>
    <Simple><Name>TITLE</Name><String>Track_Level_Title</String></Simple>
    <Simple><Name>ARTIST</Name><String>Track_Artist</String></Simple>
    <Simple><Name>PART_NUMBER</Name><String>4</String></Simple>
    <Simple><Name>TOTAL_PARTS</Name><String>11</String></Simple>
    <Simple><Name>DATE_RECORDED</Name><String>2014-05-06T07:08:09</String></Simple>
  </Tag>
</Tags>
XML
cat > tags_track.xml <<'XML'
<?xml version="1.0" encoding="UTF-8"?>
<Tags>
  <Tag>
    <Targets><TargetTypeValue>30</TargetTypeValue></Targets>
    <Simple><Name>TITLE</Name><String>Audio_Only_Title</String></Simple>
    <Simple><Name>ENCODER</Name><String>Track_Encoder</String></Simple>
  </Tag>
</Tags>
XML
g src_for_mkvmerge.mkv $V $A $ENC -map_metadata -1
"$MKVMERGE" -q -o mkvmerge_global.mkv --title Segment_Title --global-tags tags_global.xml src_for_mkvmerge.mkv >/dev/null || echo "FAILED: mkvmerge_global" >&2
"$MKVMERGE" -q -o mkvmerge_track_tags.mkv --tags 1:tags_track.xml src_for_mkvmerge.mkv >/dev/null || echo "FAILED: mkvmerge_track_tags" >&2
"$MKVMERGE" -q -o mkvmerge_attach.mkv --attachment-description "Front cover" --attachment-mime-type image/jpeg --attach-file cover.jpg --attachment-mime-type application/x-truetype-font --attach-file cover.png src_for_mkvmerge.mkv >/dev/null || echo "FAILED: mkvmerge_attach" >&2
"$MKVMERGE" -q -o mkvmerge_webm.webm --webm --title WebM_Boxed --global-tags tags_global.xml webm_tags.webm >/dev/null || echo "FAILED: mkvmerge_webm" >&2
"$MKVMERGE" -q -o mkvmerge_from_mp4.mkv --title From_MP4 tags_ilst.mp4 >/dev/null || echo "FAILED: mkvmerge_from_mp4" >&2
rm -f src_for_mkvmerge.mkv
ls | wc -l
