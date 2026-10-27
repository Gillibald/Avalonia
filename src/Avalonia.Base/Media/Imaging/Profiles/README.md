# Shipped ICC profiles

`SwopCoated.icc` (`CmykProfiles.SwopCoated`) is generated from the ANSI CGATS/SWOP TR 003-2007 characterization data (IT8.7/4, 1,617 patches, D50, 0/45), as colord distributes it in `data/profiles/SWOP_TR003_coated_3.iccprofile.xml`. The data notice reads: "Profiles, or other derivative work, based on these data may be distributed with no further permissions from CGATS. However, this Technical Report must be identified as the source of the characterization data." The profile's copyright tag names the report.

It was built with PixelMan's `gen-cmyk-profile` tool (`rust/pixelman-tests/src/bin/gen-cmyk-profile.rs`):

```
gen-cmyk-profile SWOP_TR003_coated_3.iccprofile.xml SwopCoated.icc 9 0.1 1 "SWOP Coated (CGATS TR 003), relative colorimetric with black point compensation"
```

That is a 9-point grid per ink, smoothness 0.1, neutral black point compensation; ICC v2.1 with one lut16 A2B0/A2B1 table. The output is deterministic: SHA-256 `bb10ac72033d86a493b250815ce92d920f6bc7f72f9be7bda9c36ba64dacd086` from data with SHA-256 `a9a244de075a80d6abcdcd2289b4c373b605e0aebafaafb08fff7a4f2f3b8cbd`.
