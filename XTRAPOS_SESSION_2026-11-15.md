# XtraPOS — مرجع جلسة 15 نوفمبر 2026

**التاريخ:** 15 نوفمبر 2026  
**المستودع:** `D:\Google\XtraPOS` على الفرع `main`  
**GitHub:** `https://github.com/saleem861/XtraPOS.git`  
**آخر إيداع عند كتابة هذا الملف:** `ed99338`  
**الغرض:** يقرأ عميل Cursor الجديد هذا الملف قبل أي تعديل. يجمع تطوير وكيل الفرع والاستضافة المحلية (المرجع السابق) مع ما بُني في هذه الجلسة، والحالة الفعلية للجهاز وموقع الاختبار، والاتجاه الذي يجب اتباعه.

لا تنسخ هذا الملف إلى مكان آخر فيه أسرار. لا تطبع كلمات سر أو رموز أجهزة أو محتوى `staging.env` أو `local-pos-session.json`.

---

## 1. الاتجاه المعماري الملزم

التشغيل الحقيقي صار على جهاز الفرع، لا على قاعدة موقع الاختبار.

1. المستخدم يثبّت وكيل الفرع على ويندوز. الخدمة `XtraPOSBranchAgent` تشغّل `XtraPOS.Api.exe --branch-agent` وتستضيف التطبيق كاملاً على `http://127.0.0.1:47335`.
2. معالج الإعداد يحفظ اتصال SQL الحقيقي في `App_Data\operational-branch.json` على الجهاز. هذا الاتصال هو قاعدة العمل.
3. بعد اكتمال الإعداد، الواجهة على موقع الاختبار تتحوّل تلقائياً إلى `localAppUrl` الخاص بالوكيل. الموقع البعيد يبقى لتنزيل الوكيل وربطه، ولا يصبح شاشة البيع المتصلة بقاعدة الفرع.
4. موقع الاختبار `https://test-pos.sharbelfashion.com` مربوط بقاعدة تجربة قديمة. حسابه `xtrapos_staging` وقاعدته `XtraPOS_Branch_Test_Staging`. هذه ليست قاعدة المنشأة. `OperationalDatabaseGuard` يرفض اختيارها كقاعدة تشغيل.
5. حساب إعداد الجهاز لا يُرسل إلى ملف بيئة موقع الاختبار، وموقع الاختبار لا يستطيع فتح `localhost` على جهاز الكاشير ولا استخدام مصادقة ويندوز الخاصة بذلك الجهاز.

جهاز هذه الجلسة مُعد هكذا:

| البند | القيمة |
|---|---|
| الخدمة | `XtraPOSBranchAgent` بحساب `LocalSystem` |
| المسار | `C:\Program Files\XtraPOS\BranchAgent` |
| العنوان | `http://127.0.0.1:47335` |
| الخادم المربوط | `https://test-pos.sharbelfashion.com` |
| المنشأة / الفرع | Smoker World / Sharbel |
| نقطة البيع | صندوق 1 — كاونتر 1 |
| SQL | `localhost`، النسخة الافتراضية `MSSQLSERVER` |
| القاعدة | `XtraPOS_Branch` |
| المصادقة | ويندوز، بلا مستخدم SQL وبلا كلمة سر في ملف الاتصال |
| دور القاعدة | `Branch`، المكان `Local` |

لا تحذف `XtraPOS_Branch` ولا تعِد تهيئة الجهاز إلا بطلب صريح. `NT AUTHORITY\SYSTEM` مُنح `dbcreator` على `localhost` فقط حتى يستطيع معالج الإعداد إنشاء قاعدة. لا تسحب هذه الصلاحية إلا بطلب صريح. نسخة `localhost\BARTENDER` نسخة أخرى ولا تُستخدم لهذا الجهاز.

---

## 2. ما بُني قبل هذه الجلسة: الوكيل المحلي والانتقال للتطبيق

هذا ملخص المرجع الهندسي الذي سُلّم في 15 نوفمبر 2026. التفاصيل الأصلية فيه، والكود في المسارات المذكورة.

### 2.1 الطبقات

| الطبقة | المسار | الدور |
|---|---|---|
| الواجهة | React + TypeScript + Vite، `src/XtraPOS.Web` | عربية RTL، الإعداد، البيع، الزبائن، الدين، العضوية. المسودات في IndexedDB |
| الخادم | ASP.NET Core 8، `src/XtraPOS.Api` | `/api/*`، الترحيلات، كوكي `XtraPOS.Auth`، البيع والدين والمزامنة والوكيل |
| المثبّت | `src/XtraPOS.BranchAgent.Setup` | `XtraPOS-BranchAgent-Setup.exe` وخدمة `XtraPOSBranchAgent` |
| SQL | `Arabic_CI_AS` | قاعدة لكل فرع، وقاعدة مركز اختيارية للكتالوج والعضوية الموحّدة. المتصفح لا يتصل بـ SQL |
| الاختبارات | xUnit و Vitest و oxlint | قواعد العمل، الأمان، الترحيلات، التسعير، السلة، الإعداد |

ترحيلات الفرع في `src/XtraPOS.Api/Data/Migrations` حتى `045_ItemPriceUniqueness.sql`. ترحيلات المركز في `src/XtraPOS.Api/Data/Central/Migrations` حتى `031_ItemPriceUniqueness.sql`.

### 2.2 حماية الاتصال

- `OperationalDatabaseGuard` يرفض القواعد التجريبية والمحمية: `master`، `model`، `msdb`، `tempdb`، `XtraPOS_Central`، `XtraPOS_Branch_01`، `XtraPOS_Branch_02`، `XtraPOS_Branch_Test_Staging`، `XtraPOS_Branch_Test_Cloud`، `saleemPOSDB`، `Shop1`، `Shop2`، `swstore`. الأسماء التشغيلية مثل `XtraPOS_Branch` و `XtraPOS_Branch2` مقبولة.
- `SqlConnectionFactory.IsSafeConfiguration` يقدّم الاتصال التشغيلي النشط في `operational-branch.json` على الاسم الافتراضي في `appsettings`. لذلك يعمل الوكيل بعد الإعداد حتى لو حُذفت ملفات الإعداد المنشورة.
- كلمة سر الاتصال المحلي تُحفظ بـ DPAPI داخل `operational-branch.json`. رموز الأجهزة تُحفظ مجزأة. كوكي الدخول `HttpOnly` وسياسته `CookieSecurePolicy.SameAsRequest` حتى يعمل على `https` وعلى `http://127.0.0.1:47335`.

### 2.3 الوظائف المبنية

- معالج الإعداد: فرع مستقل أو مرتبط بمركز، إنشاء المنشأة والفرع ونقطة البيع والجهاز والمدير.
- حالات `GET /api/setup/status`: `NotConfigured`، `Configuring`، `Configured`، `ConfiguredButAdminMissing`، `DatabaseUnavailable`، `Error`.
- البيع: بحث الأصناف، تعديل الكمية والسعر والإجمالي، الأصناف الموزونة، الدفع النقدي والبطاقة والمقسّم، مسودات السلة، الوردية، طباعة A4، مراجعة المبيعات بتوقيت `Asia/Hebron`.
- الزبائن والدين: التسجيل، OTP، البيع الآجل في دفتر الفرع ثم المزامنة، التحصيل مع وردية أو بدونها، العكس.
- العضوية: QR، Google، Apple، Passkeys، وأسعار العضوية على السلة.
- الاستيراد والمزامنة وعمال الخلفية. لا يُقفل إقلاع الوكيل قبل اكتمال الإعداد.

### 2.4 مشكلة الموقع البعيد والحل الذي بُني

الموقع على Linux لا يصل إلى SQL المحلي على ويندوز (`localhost` أو `localhost\BARTENDER`). بعد إعداد الوكيل كانت العودة إلى الموقع البعيد تُبقي المستخدم على شاشة الإعداد.

الحل المبني:

1. المثبّت ينشئ الخدمة ويتحقق من SHA-256 لحزمة `branch-agent.zip`. يحاول منح `CREATE ANY DATABASE` لحساب `NT AUTHORITY\SYSTEM` من دون المساس بقواعد المستخدم القائمة.
2. الوكيل يستضيف الواجهة والـ API على `http://127.0.0.1:47335`. الثوابت في `BranchAgentHostCommand`: `LocalAppPort = 47335` و `LocalAppUrl = "http://127.0.0.1:47335"`.
3. قبل وجود قاعدة مُعدّة، سلسلة الاتصال الاحتياطية هي `localhost/master` حتى تقلع الخدمات.
4. `wwwroot` يأتي من بناء الواجهة ويُنسخ داخل حزمة الوكيل في `deploy/Deploy-StagingDirect.ps1`.
5. `GET` حالة الوكيل يعيد `ConfiguredBranch` و `LocalAppUrl` بعد الإعداد وعند وجود `wwwroot\index.html`.
6. الربط `POST /pair` برمز من 8 خانات ست عشرية، صالح 10 دقائق ولمرة واحدة. نقل الربط إلى خادم آخر يحتاج `confirmTransfer` وإلا 409.
7. الواجهة: إذا كانت حالة الموقع البعيد `NotConfigured` والوكيل مُعداً و`localAppUrl` مختلفاً عن العنوان الحالي ولا يوجد `?setup=1`، يتم `window.location.replace(localAppUrl)`. مع `?setup=1` تبقى شاشة الحالة وفيها زر «فتح التطبيق الرئيسي».

ملفات هذه المرحلة: `SqlConnectionFactory.cs`، `BranchAgentLocalEndpoint.cs`، `BranchAgentHostCommand.cs`، `BranchAgentWorker.cs`، `BranchAgentExecutor.cs`، `Program.cs`، `Deploy-StagingDirect.ps1`، `branchAgentLocal.ts`، `App.tsx`، `ApplicationRoot.tsx`، `SetupChoiceScreen.tsx`، واختبارات `BranchAgentLocalTests.cs` و `SqlConnectionSafetyTests.cs` و `applicationRoot.test.tsx`.

---

## 3. ما بُني في هذه الجلسة: عضوية Google على جهاز جديد

المطلوب كان جذرياً: أي جهاز جديد يُثبَّت عليه الوكيل ثم يُعد يكون جاهزاً لتسجيل عضوية Google من دون تعديل يدوي على ملف الإعداد.

### 3.1 العطل الأول: «التحقق من العضوية غير متاح»

السبب: الوكيل المثبت كان `Membership.Enabled` و `GoogleLoginEnabled` بقيمة `false`، ونسخة الملف التنفيذي القديمة لم تكن تفرض التفعيل. السجل كتب `MEMBERSHIP_SERVER_UNAVAILABLE`.

التصحيح في `4eb83fb` ثم `657fdcd`:

- `src/XtraPOS.Api/Setup/BranchAgentMembershipFile.cs` يكتب في `appsettings.json` عند التثبيت: العضوية وGoogle مفعّلان، Apple والتحقق للأسعار الخاصة مطفآن، ويضع معرّف Google العام إذا كان فارغاً، ويضبط `PublicOrigin` على عنوان يصل إليه الهاتف.
- المعرّف العام، وهو ليس سراً: `998725042091-t1p0fv71s7ajkbs104s62if1c1oviqmd.apps.googleusercontent.com`.
- `MembershipLinks.PhoneOrigin` يفوّض إلى `BranchAgentMembershipFile.PhoneOrigin`. العنوان المحلي `127.0.0.1` ليس عنوان هاتف. العنوان العام المستخدم هو `https://test-pos.sharbelfashion.com`.
- مثبّت الوكيل يترجم هذا الملف مباشرة ولا يرجع إلى تجميعة الـ API. محاولة استدعاء نوع داخل `XtraPOS.Api.Membership` من المثبّت كسرت النشر بخطأ `CS0234`. `657fdcd` أخرج اختيار العنوان من تجميعة العضوية حتى يُبنى المثبّت.
- `Program.cs` يفرض نفس التفعيل لبيئة الاختبار ولعملية `--branch-agent`.
- `deploy/Deploy-StagingDirect.ps1` يكتب نفس القيم في `appsettings` الخاص بحزمة الوكيل بعد حذف ملفات `appsettings.*.json`. استخدم `Add-Member -MemberType NoteProperty -Name -Value`. الصيغة `-NotePropertyName` فشلت لأنها غامضة.
- الاختبار: `Installed_agent_settings_enable_google_membership` و `Reinstall_replaces_a_local_membership_address_with_the_site` في `BranchAgentLocalTests.cs`.

بعد التثبيت، `GET http://127.0.0.1:47335/api/membership/config` أعاد التفعيل و`publicOrigin` للموقع العام.

### 3.2 العطل الثاني: «تعذر تجهيز رابط العضوية على الخادم العام»

السلوك الحالي في الكود:

1. زر «تسجيل عضوية Google/Apple» في شاشة الزبون، قبل حفظ الزبون، مسموح. `CustomerId` يكون فارغاً.
2. الوكيل ينشئ الصف محلياً ثم، إذا كان عنوان الهاتف عاماً، يستدعي `BranchAgentWorker.PublishEnrollmentAsync`.
3. الطلب `POST /api/branch-agent/membership/enrollment` على الموقع العام، برمز الوكيل. الطلب المجهول يعيد 401، وهذا يثبت أن المسار منشور.
4. الرسالة العربية تُعرض عندما يعيد النشر `false`.

ما رُصد في `C:\Program Files\XtraPOS\BranchAgent\App_Data\myLog.txt`:

- قبل النشر الأول: `public-publish-failed` بلا جسم.
- بعد إضافة تسجيل الرد: `public-publish-503 {"code":"MEMBERSHIP_SERVER_UNAVAILABLE"}`.
- بعد نشر `51151e2`: `public-publish-503 {"code":"MEMBERSHIP_SQL_4060"}`.

معنى 4060: لا يمكن فتح قاعدة البيانات المطلوبة باسم الدخول. موقع الاختبار أعاد أيضاً `GET /api/setup/status` بحالة `NotConfigured`، وفحص `/health` بحالة `Healthy`. الصحة لا تفتح SQL.

التصحيحات التي دُفعت ثم نُشرت:

| الإيداع | ماذا يفعل |
|---|---|
| `4c47dad` | فتح تسجيل العضوية على الموقع العام بدل عنوان جهاز الكاشير |
| `d2727a3` | إبقاء الجهاز الجديد على موقع العضوية العام |
| `17ce851` | استيراد Vitest في اختبار عنوان الهاتف |
| `51151e2` | إذا لم تكن قاعدة المركز مربوطة والعضوية مفعّلة، استخدم قاعدة الموقع نفسه. فشل SQL صار يرد `MEMBERSHIP_SQL_` مع رقم الخطأ بدل إخفائه |
| `7d5545d` | إذا فشل فتح قاعدة المركز والعضوية مفعّلة، احفظ الرابط في قاعدة الموقع نفسه عندما تختلف سلسلة الاتصال |

`OpenCentralAsync` في `MembershipService.cs`: عملية الوكيل المحلي (`LocalAppHostState.BaseUrl` غير فارغ) تفتح قاعدة الفرع المحلية. عملية الموقع العام تحاول المركز، ثم قاعدتها نفسها عند فشل الفتح.

جدول `MembershipEnrollmentRequests` في ترحيل الفرع `043` يقبل `CustomerID` فارغاً وفيه `CashierUserID`. هذا يناسب التسجيل قبل حفظ الزبون.

### 3.3 ماذا أثبت النشر

أوامر النشر التي شغّلها المستخدم من جهازه. لا يُعاد تشغيلها إلا بطلبه.

| الإصدار المنشور | الإيداع | الملاحظة |
|---|---|---|
| `20261115-152816-657fdcd` | `657fdcd` | نُشر بعد إصلاح بناء المثبّت |
| `20261115-154731-51151e2` | `51151e2` | الخدمة `active`، العضوية العامة مفعّلة، وGoogle مفعّل |
| `20261115-160417-7d5545d` | `7d5545d` | التفعيل نجح، لكن ترحيل القاعدتين لم يُطبَّق |

نص التفعيل الحرفي:

```text
Branch database 'XtraPOS_Branch_Test_Staging' is not available to the staging login. No database was created and no migration was applied.
Applying central migrations for database 'XtraPOS_Central_Test_Staging'. No database will be dropped.
Central database 'XtraPOS_Central_Test_Staging' is not available to the saved login. No database was created and no migration was applied.
ACTIVATE_OK=20261115-160417-7d5545d
```

السبب في الكود: `Program.cs` يفتح القاعدة أولاً عبر `PrintCountsAsync`. الخطأ 4060 أو 911 يُلتقط ويُطبع أن القاعدة غير متاحة، ولا يُستدعى `EnsureBranchDatabaseAsync`، ورمز الخروج يبقى 0 فيكمل تبديل النسخة. حساب `xtrapos_staging` يفتح `master` ولا يملك `CREATE DATABASE`. لا يُمنح `dbcreator`. حساب التجهيز المصمم اسمه `xtrapos_provisioner` وصلاحيته `CREATE ANY DATABASE`، ولم يُؤكد وجوده في بيئة الاختبار.

خادم SQL الخاص بموقع الاختبار: `69.30.235.148,10173`. مستخدم التشغيل `xtrapos_staging`. مستخدم المركز المحفوظ `xtrapos_staging_central`. كلمة السر في `/etc/xtrapos/staging.env` فقط. لا تقرأ هذا الملف ولا تطبعه.

اسم القاعدة فقط يمكن قراءته بالأمر المعتمد على الخادم: `sudo -n /usr/local/sbin/xtrapos-staging-db-name`. آخر قراءة أعادت `DB_NAME=XtraPOS_Branch_Test_Staging`.

### 3.4 السكربت الذي أُضيف ثم اتضح أنه ليس حل التشغيل الحقيقي

`ed99338` أضاف `deploy/staging-branch-provision.sql`. يشغّله مدير SQL على `master`، وينشئ `XtraPOS_Branch_Test_Staging` إذا كانت غائبة، ويمنح `xtrapos_staging` ملكية هذه القاعدة فقط، ولا ينشئ حساب الدخول ولا يمنحه دوراً على الخادم. سكربت المركز الموجود سابقاً هو `deploy/staging-central-provision.sql`.

هذا يجهّز قاعدة موقع الاختبار القديمة. المستخدم أوضح بعد ذلك أن المرحلة التجريبية انتهت، وأن الحسابات الحقيقية هي التي أُدخلت في إعداد الجهاز. لا تشغّل سكربت التجهيز ولا تنشئ `XtraPOS_Branch_Test_Staging` لإصلاح زر العضوية. لا تغيّر ملف بيئة الموقع ليشير إلى `localhost` أو إلى `XtraPOS_Branch` على جهاز الكاشير.

---

## 4. الحالة التي يجب أن يكمل منها العميل الجديد

زر «تسجيل عضوية Google/Apple» على الجهاز ما زال يظهر: «تعذر تجهيز رابط العضوية على الخادم العام.»

السبب المفهوم الآن:

- الوكيل يجهّز الرمز ثم يرسله إلى موقع الاختبار لأن عنوان `127.0.0.1` لا يفتحه هاتف الزبون.
- موقع الاختبار يحاول حفظ الصف في قاعدته التجريبية، وهي غير متاحة لحسابه.
- قاعدة العمل الحقيقية `XtraPOS_Branch` على جهاز الكاشير لا يستقبلها هذا الطلب.

الاتجاه التالي، ولم يُنفَّذ بعد:

- سجل العضوية والزبون يبقى في قاعدة الفرع الحقيقية التي حفظها الإعداد.
- صفحة الهاتف تبقى على عنوان عام لأن الهاتف لا يصل إلى `127.0.0.1`.
- الموقع العام لا يصبح قاعدة تشغيل المنشأة، ولا يُستخدم `xtrapos_staging` بدل حساب الجهاز.
- أي جهاز جديد يُثبَّت ويُعد يجب أن يصل إلى هذه النتيجة بلا تعديل يدوي لملفاته.

لا تُعلن أن الإنتاج جاهز. لا تنشر إلى موقع الاختبار إلا إذا طلب المستخدم النشر صراحة. أمر النشر الذي استخدمه:

```powershell
powershell -ExecutionPolicy Bypass -File D:\Google\XtraPOS\deploy\Deploy-StagingDirect.ps1
```

النشر لا يكتب المركز إلا مع `-WriteCentral`، وهذا لم يُطلب. خدمة الإنتاج `xtrapos-api.service` على المنفذ `5216` لا تُمس.

---

## 5. الجهاز المحلي وSQL

- ويندوز على هذا الجهاز. مستخدم الجلسة التفاعلي مدير على `localhost` وعلى `localhost\BARTENDER`.
- `MSSQLSERVER` كانت تفشل عند الإقلاع بسبب شهادة مفقودة (`0x80092004`) ثم فشل إنشاء شهادة بديلة لحساب الخدمة الافتراضي. المستخدم حوّل حساب الخدمة إلى `Local System` من SQL Server Configuration Manager. لا تعِد الحساب الافتراضي.
- معالج الإعداد بحساب الخدمة `SYSTEM` لم يكن يستطيع إنشاء قاعدة على `localhost`. مُنح `dbcreator` هناك. الفحص الذي يقبله المعالج لنية «قاعدة جديدة» هو عضوية `dbcreator` أو `sysadmin`، بينما سياسة المنتج تفضّل حساب تجهيز منفصل بصلاحية `CREATE ANY DATABASE` فقط. الفحص الحي هو الذي طُبّق على هذا الجهاز.
- أُعيد تثبيت الوكيل في هذه الجلسة بعد حذف الخدمة والمجلد بطلب المستخدم، ثم أعاد المستخدم الإعداد. الهوية الحالية للوكيل في `App_Data\branch-agent.identity.json` تشير إلى موقع الاختبار. لا تطبع `branch-agent.token` ولا جلسة نقطة البيع.
- مجلد Google Drive يكتب `desktop.ini` داخل `.git`، ومنها `.git\refs\desktop.ini`. `git fetch` و`git commit` يطبعان `bad object refs/desktop.ini` ويفشل `geometric-repack`، لكن الإيداع والدفع إلى `origin/main` نجحا. لا تحذف هذه الملفات بالجملة ولا تعمل `push --force`.

المزامنة عند كتابة الملف: `main` المحلي و`origin/main` على `ed99338`، وشجرة العمل نظيفة قبل إضافة هذا الملف.

---

## 6. قواعد لا يتجاوزها العميل الجديد

- الواجهة من `XTRAPOS_UI_STANDARD.md`: لا تغيّر CSS العام ولا لوحات المفاتيح ولا شاشات البيع والزبائن والدين من دون مهمة تخصها.
- لا تشر إلى قواعد الإنتاج المحمية ولا تنشئها ولا تهاجرها ولا تحذفها: `XtraPOS_Central`، `XtraPOS_Branch_01`، `XtraPOS_Branch_02`، `saleemPOSDB`، `Shop1`، `Shop2`، `swstore`، وأي قاعدة إنتاج.
- لا تنشئ `XtraPOS_Central` في بيئة وكيل السحابة. قاعدة تلك البيئة الوحيدة هي `XtraPOS_Branch_Test_Cloud` على `127.0.0.1:1433`.
- منافذ التطوير المحلي تبقى Vite `5173` وAPI `5080`. لا تبدأ نسخة ثانية إذا كانت إحداهما تعمل. وكيل الفرع منفذ مستقل `47335`.
- لا تودع `.env` ولا `staging.env` ولا كلمات السر ولا المفاتيح.
- بعد انتهاء التعديل: إيداع الملفات المتعلقة فقط، ثم `git push` إلى `origin/main` من دون force. النشر إلى موقع الاختبار طلب منفصل.
- تحقق من سلوك الواجهة في المتصفح عندما يتغير ما يراه المستخدم. لقطة واحدة للشاشة لا تكفي.

---

## 7. إيداعات 15 نوفمبر المتعلقة بالعضوية

```text
4eb83fb  Make a new branch agent install ready for Google membership.
4c47dad  Open membership enrollment on the public site instead of the cashier PC.
d2727a3  Keep a new branch device on the public membership site.
17ce851  Import vitest in the phone membership URL test.
657fdcd  Let the branch agent installer compile without the API membership assembly.
51151e2  Store a new device membership link on the public server even when central is not linked.
7d5545d  Keep a phone membership link on the public server when its central database cannot be opened.
ed99338  Add the administrator script that prepares the staging branch database for the existing runtime login.
```

`4eb83fb` حتى `7d5545d` منشورة أو مضمّنة في النسخة المنشورة `20261115-160417-7d5545d`. `ed99338` على GitHub فقط، وهو سكربت SQL لم يُشغَّل.
