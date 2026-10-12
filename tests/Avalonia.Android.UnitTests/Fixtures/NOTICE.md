# Fixture notices

The `aosp-api*.xml` files are unmodified font configuration files from the Android Open Source Project (`frameworks/base/data/fonts/fonts.xml` and, for API 35, `font_fallback.xml`):

| File | Source |
|---|---|
| `aosp-api24-fonts.xml` | tag `android-7.0.0_r1` |
| `aosp-api26-fonts.xml` | tag `android-8.0.0_r1` |
| `aosp-api28-fonts.xml` | tag `android-9.0.0_r1` |
| `aosp-api29-fonts.xml` | tag `android-10.0.0_r1` |
| `aosp-api30-fonts.xml` | `/system/etc/fonts.xml` of the Android 11 emulator system image, identical to tag `android-11.0.0_r1` |
| `aosp-api31-fonts.xml` | tag `android-12.0.0_r1` |
| `aosp-api33-fonts.xml` | tag `android-13.0.0_r1` |
| `aosp-api34-fonts.xml` | tag `android-14.0.0_r1` |
| `aosp-api35-fonts.xml`, `aosp-api35-font_fallback.xml` | tag `android-15.0.0_r1` |
| `aosp-api36-fonts.xml` | tag `android-16.0.0_r1` |

Copyright (C) The Android Open Source Project

Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.

The other XML files in this directory are written for the tests. They reproduce the structure of vendor configurations (file names on their own lines, commented-out families, variable font axes, collection indices per language, `ignore` and `fallbackFor` attributes) without copying vendor files.
