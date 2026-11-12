# XtraPOS — تسليم العمل في 12 نوفمبر 2026

هذا الملف يصف المشروع كما هو على القرص وعلى موقع الاختبار، حتى يكمل العمل طرف آخر.
لا يحتوي كلمات سر قواعد البيانات ولا سر Google ولا المفتاح الخاص. هذه القيم ليست مكتوبة في Git، ونسخها إلى هذا الملف يعرضها للرفع. أماكنها مذكورة في قسم الاتصال.

## 1. ما هو المنتج

XtraPOS نظام نقاط بيع للفروع.

- الواجهة: React و TypeScript و Vite في `src/XtraPOS.Web`. الاتجاه من اليمين لليسار.
- الخادم: ASP.NET Core 8 في `src/XtraPOS.Api`، الإصدار `0.3.0`.
- البيانات: SQL Server، تجميع `Arabic_CI_AS`. قاعدة فرع لكل محل. قاعدة مركز منفصلة للعضوية والكتالوج عند الحاجة.
- المتصفح لا يستلم سلسلة اتصال SQL. هوية الجهاز رمز يُحفظ مرة واحدة، و SQL يخزن تجزئته فقط. جلسة الموظف كوكي `HttpOnly` باسم `XtraPOS.Auth`.
- يوم العمل على شاشة مراجعة المبيعات يتبع `Asia/Hebron`. كثير من تحويلات SQL تستخدم `Israel Standard Time`.
- عبارة «متصل بخادم الفرع» نص ثابت ولا تُغيّر.
- الزبون لا يُعرض كـ GUID.
- `BranchRemoteSync.IsImplemented` يبقى `false`.

مرجع السلوك القديم تطبيق VB.NET في `D:\XtraposSource\XtraPOS`، وأهم ملفاته `SalesFormArabic.vb` و `OrdersFormArabic.vb`.

## 2. هيكل المستودع

الجذر: `D:\Google\XtraPOS`. الحل: `XtraPOS.sln`.

| المسار | الدور |
|---|---|
| `src/XtraPOS.Web` | شاشات نقاط البيع ومعالج الإعداد. `npm run dev` و `npm test` و `npm run build` |
| `src/XtraPOS.Api` | واجهة HTTP والترحيلات والمبيعات والدين والعضوية والإعداد |
| `src/XtraPOS.BranchAgent.Setup` | مثبّت خدمة الوكيل على ويندوز. الاسم الناتج `XtraPOS-BranchAgent-Setup.exe` |
| `src/XtraPOS.Api/Data/Migrations` | ترحيلات الفرع، من `001_InitialSchema.sql` حتى `045_ItemPriceUniqueness.sql` |
| `src/XtraPOS.Api/Data/Central/Migrations` | ترحيلات المركز، من `001_CentralFoundation.sql` حتى `031_ItemPriceUniqueness.sql` |
| `src/XtraPOS.Api/Setup` | معالج الإعداد، إنشاء القاعدة المختارة، ووكيل الفرع |
| `src/XtraPOS.Api/Sales` | البيع والجلسات النقدية |
| `src/XtraPOS.Api/Credit` | دفتر الدين والتحصيل |
| `src/XtraPOS.Api/Customers` | الزبائن والتحقق |
| `src/XtraPOS.Api/Membership` | العضوية و Google و Apple و Passkey |
| `src/XtraPOS.Api/Sync` | المزامنة. المزامنة البعيدة للفرع غير مفعّلة |
| `src/XtraPOS.Api/Catalog` | الكتالوج. يوجد `StagingCatalogSeed.cs` غير متتبع ولا يُحذف |
| `tests/XtraPOS.Api.Tests` | اختبارات xUnit |
| `deploy` | نشر الاختبار فقط |
| `database` | ملاحظات. لا ملفات قاعدة هنا |
| `docs` | ملاحظات التصميم والدين |
| `.github/workflows/ci.yml` | فحص GitHub |
| `.github/workflows/deploy-staging.yml` | نشر الاختبار عبر بيئة `staging` المحمية. لا يُشغَّل إلا بموافقة صريحة |
| `.ssh` | مفتاح نشر الاختبار. داخل `.gitignore` |

منافذ التطوير المحلية ثابتة:

- الواجهة `http://127.0.0.1:5173`
- الـ API `http://127.0.0.1:5080` بملف الإطلاق `http` و `ASPNETCORE_ENVIRONMENT=Development`
- Vite يمرر `/api` و `/health` إلى 5080
- الصحة: `{"status":"Healthy"}`
- لا تُشغَّل نسخة ثانية من Vite أو الـ API
- أوقف العملية على 5173 قبل `npm ci` لأن Windows يقفل ملفات `node_modules`

أوامر من جذر المستودع:

```powershell
dotnet build XtraPOS.sln -c Debug
dotnet run --project src\XtraPOS.Api\XtraPOS.Api.csproj --launch-profile http
```

من `src\XtraPOS.Web`:

```powershell
npm run dev
npm test
npm run build
```

حزم NuGet تُستعاد إلى `%USERPROFILE%\.nuget\packages`. لا تستخدم مجلد `cursor-sandbox-cache`.

آخر commit معروف أثناء هذا العمل: `2969d0d81354af584ad37d3736075480419a4eda`. شجرة العمل كانت غير نظيفة، لذلك اسم الإصدار ينتهي بـ `2969d0d` حتى لو احتوى ملفات غير مُودعة. لا تعمل commit إلا بطلب صريح. لا تعمل `git reset` أو `git clean` أو `git stash`.

ملفات غير متتبعة يجب ألا تُحذف:

- `src/XtraPOS.Api/Catalog/StagingCatalogSeed.cs`
- `src/XtraPOS.Api/Data/Central/Migrations/024_MembershipCounterCode.sql`
- `src/XtraPOS.Api/Data/Central/Migrations/025_MembershipCounterDay.sql`
- `src/XtraPOS.Api/Membership/MembershipCalendar.cs`
- `src/XtraPOS.Web/src/membership/savedIdentity.ts`

عمل تصنيفات الزبائن وقائمة رموز الأسعار وافتراضات الخصم واتصال المركز يملكه مسار آخر. لا يُستبدل.

معيار الواجهة في `XTRAPOS_UI_STANDARD.md`. لا تُغيّر CSS ولا الألوان ولا الخطوط ولا الأبعاد ولا الاتجاه ولا `FullKeyboard` ولا `NumericKeypad` ولا شاشات البيع والزبائن والدين من أجل إضافة وظيفة.

## 3. البيئات

لا توجَّه بيئة إلى قاعدة بيئة أخرى.

| البيئة | الموقع | الخدمة | قاعدة الفرع | قاعدة المركز |
|---|---|---|---|---|
| جهاز التطوير | `http://127.0.0.1:5173` | `http://127.0.0.1:5080` | ما يحدده إعداد الجهاز. مصادقة ويندوز إذا لم يُضبط مستخدم SQL | لا تُستخدم لشاشة البيع المحلية |
| وكيل السحابة | نفس المنفذين | نفس المنفذين | `XtraPOS_Branch_Test_Cloud` على `127.0.0.1:1433` فقط | ممنوعة. لا تُنشأ `XtraPOS_Central` |
| الاختبار | `https://test-pos.sharbelfashion.com` | داخل الخادم `127.0.0.1:5088`، الوحدة `xtrapos-staging.service` | `XtraPOS_Branch_Test_Staging` | `XtraPOS_Central_Test_Staging` |
| الإنتاج | لا يُستخدم في هذا العمل | `xtrapos-api.service` المنفذ `5216` والمسار `/opt/xtrapos-api` | لا تُمس | لا تُمس |

في 12 نوفمبر 2026:

- Vite على 5173 متوقف بطلب سابق.
- الـ API المحلي على 5080 أُوقف لأن ملف `XtraPOS.Api.exe` كان مقفلاً أثناء البناء. لا تعِد تشغيله على قاعدة إنتاج.
- موقع الاختبار سليم على الإصدار المذكور في القسم 8.

## 4. الاتصال بخادم Linux لموقع الاختبار

لا توجد كلمة سر لدخول النشر. الدخول بمفتاح SSH.

| البند | القيمة |
|---|---|
| الموقع | https://test-pos.sharbelfashion.com |
| عنوان الخادم | 69.197.178.239 |
| مستخدم النشر | xtrapos-deploy |
| المفتاح الخاص | `D:\Google\XtraPOS\.ssh\xtrapos_staging_deploy` |
| المفتاح العام | `D:\Google\XtraPOS\.ssh\xtrapos_staging_deploy.pub` |
| Git Bash | `C:\Program Files\Git\bin\bash.exe` |
| خدمة الاختبار | `xtrapos-staging.service` |
| منفذ الـ API داخل الخادم | 127.0.0.1:5088 |
| جذر الإصدارات | `/opt/xtrapos-staging` |
| الإصدار الشغال | `/opt/xtrapos-staging/current` |
| واجهة الموقع | `/opt/xtrapos-staging/current/web` |
| مفاتيح حماية البيانات | `/opt/xtrapos-staging/shared/data-protection-keys` |
| بيئة التشغيل | `/etc/xtrapos/staging.env` بصلاحية 600 ومالكها administrator. مستخدم النشر لا يقرأها |
| تفعيل الإصدار | `sudo -n /usr/local/sbin/xtrapos-staging-activate activate <version>` |
| اسم قاعدة الاختبار | `sudo -n /usr/local/sbin/xtrapos-staging-db-name` ويطبع `DB_NAME=XtraPOS_Branch_Test_Staging` فقط |
| sudo المسموح | الأمران أعلاه فقط. أي مسار تحت `/opt/xtrapos-api` مرفوض |
| فحص الصحة العام | https://test-pos.sharbelfashion.com/health |
| فحص الصحة الداخلي | http://127.0.0.1:5088/health |
| الإنتاج الممنوع | الخدمة `xtrapos-api.service`، المنفذ 5216، المسار `/opt/xtrapos-api` |

أمر النشر من أي مجلد، ومن دون `-WriteCentral`:

```powershell
powershell -NoProfile -File "D:\Google\XtraPOS\deploy\Deploy-StagingDirect.ps1"
```

السكربت ينفّذ: التأكد من الكتابة في `/opt/xtrapos-staging/releases`، ثم `npm ci` و `npm test` و `npm run build`، ثم `dotnet publish` للـ API، ثم حذف ملفات الأسرار، ثم نشر وكيل الفرع إلى `web/downloads`، ثم `deploy/scan-release.sh`، ثم النسخ، ثم التفعيل. إذا لم تصبح الخدمة سليمة يُعاد المؤشر إلى الإصدار السابق. بعد النجاح يُغيّر حقل `migrations` في `release.json` من `not-applied` إلى `applied-on-activate`.

`-WriteCentral` يطلب كلمة سر مستخدم `xtrapos_staging_central` تفاعلياً ولا يُستخدم إلا عند تغيير اتصال قاعدة المركز. لا تمرر هذه الكلمة في أمر أو ملف.

أسرار GitHub لبيئة `staging`، والقيم غير السرية منها:

- `STAGING_SSH_HOST` = `69.197.178.239`
- `STAGING_SSH_USER` = `xtrapos-deploy`
- `STAGING_SSH_KEY` و `STAGING_KNOWN_HOSTS` داخل GitHub فقط

قالب البيئة بلا أسرار: `deploy/staging.env.example`.

```
ASPNETCORE_ENVIRONMENT=Staging
SqlServer__Server=69.30.235.148,10173
SqlServer__Database=XtraPOS_Branch_Test_Staging
SqlServer__User=xtrapos_staging
Passkey__RpId=test-pos.sharbelfashion.com
Passkey__PublicBaseUrl=https://test-pos.sharbelfashion.com
DataProtection__KeyPath=/opt/xtrapos-staging/shared/data-protection-keys
DataProtection__ApplicationName=XtraPOS.Staging
```

`SqlServer__Password` موجود فقط داخل `/etc/xtrapos/staging.env`. لا يوجد مفتاح منفصل للمنفذ. المنفذ `10173` جزء من `SqlServer__Server`.

حساب `xtrapos_staging` يستطيع فتح `master` ولا يملك `CREATE DATABASE`. آخر فحص أعاد SQL 4060 على `XtraPOS_Branch_Test_Staging` وعلى `XtraPOS_Central_Test_Staging`. لا تُمنح `dbcreator` لحساب التشغيل. حساب التجهيز المصمم اسمه `xtrapos_provisioner` وصلاحياته `CREATE ANY DATABASE` عبر قسم `SqlProvisioning`، ولم يُؤكد وجوده في بيئة الاختبار.

عند التفعيل، غياب القاعدة برمز 4060 أو 911 لا ينشئ قاعدة ولا يطبّق ترحيلاً، ثم يكمل التفعيل. لا تنشئ `XtraPOS_Branch_Test_Staging` يدوياً لإصلاح ذلك.

Google للعضوية على الاختبار:

- معرّف العميل العام: `998725042091-t1p0fv71s7ajkbs104s62if1c1oviqmd.apps.googleusercontent.com`
- سر العميل داخل ملف بيئة الخادم فقط
- الرجوع: `https://test-pos.sharbelfashion.com/api/membership/auth/google/callback`
- Apple معدّ ومتوقف. رجوعه: `https://test-pos.sharbelfashion.com/api/membership/auth/apple/callback`

علم العضوية الفعلي على الاختبار يأتي من بيئة `Staging` في البرنامج: العضوية ودخول Google مفعّلان، و Apple متوقف، وأسعار الخاصة لا تشترط التحقق. القالب `staging.env.example` ما زال يكتب `Membership__Enabled=false`، والملف على الخادم هو المرجع.

## 5. قواعد البيانات

التجميع المطلوب: `Arabic_CI_AS`.

### قواعد يُسمح بالعمل عليها

| القاعدة | المكان | الحالة في 12 نوفمبر 2026 |
|---|---|---|
| `XtraPOS_Branch_Test_Staging` | `69.30.235.148,10173` | اسم التشغيل الرسمي للاختبار. الكتالوج غير متاح لحساب `xtrapos_staging`. لا تُنشأ أثناء النشر |
| `XtraPOS_Central_Test_Staging` | نفس الخادم، مستخدم `xtrapos_staging_central` | غير متاحة بنفس أسلوب 4060. لا تُنشأ أثناء النشر |
| `XtraPOS_Branch_Test_Cloud` | `127.0.0.1:1433` فقط، مستخدم `xtrapos_dev` | لبيئة وكيل السحابة. السر في `/etc/xtrapos/dev-sql.env` بصلاحية 640 |

### قواعد ممنوعة

لا تشير الـ API إليها ولا تنشئها ولا تهاجرها ولا تحذفها:

- `XtraPOS_Central` على جهاز التطوير أو وكيل السحابة
- `XtraPOS_Branch_01`
- `XtraPOS_Branch_02`
- `saleemPOSDB`
- `Shop1`
- `Shop2`
- `swstore`
- أي قاعدة إنتاج على خدمة `xtrapos-api.service`

على جهاز ويندوز المحلي `DESKTOP-MFTEQK0`:

- النسخة الافتراضية `localhost` واسمها `MSSQLSERVER`، وهي تعمل.
- النسخة المسماة `localhost\BARTENDER`، وهي MSSQL12 وتعمل.
- الدخول بمصادقة ويندوز للمستخدم `DESKTOP-MFTEQK0\Saleem Jayousi`، وهو sysadmin، و `CREATE ANY DATABASE` متاح له.
- سلسلة الاتصال المحلية: `Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True`
- `shop1` موجودة على النسخة الافتراضية. لا تُحذف ولا تُعدَّل.
- `XtraPOS_Branch_94` و `XtraPOS_Branch_95` و `XtraPOS_Central_Test_LocalOps` لا تُحذف.
- `XtraPOS_Branch_96` و `XtraPOS_Branch_97` كانتا قاعدتي اختبار مؤقتتين وأُسقطتا بعد الفحص. لا تعِد إنشاءهما من أجل النشر.
- مصادقة ويندوز نحو `69.30.235.148,10173` تفشل لأن النطاق غير موثوق.
- `DB_ID` حين لا توجد القاعدة يعيد `DBNull` وليس `null` في C#.

أسماء يرفضها `OperationalDatabaseGuard` لكتالوج تشغيلي مختار: أي اسم فيه Test أو Demo أو Integration أو Temporary أو Staging، والأسماء المحمية `XtraPOS` و `XtraPOS_Central` وما يتفرع عنه و `XtraPOS_Branch_01` و `XtraPOS_Branch_02` و `XtraPOS_Branch_Test_Staging` و `XtraPOS_Branch_Test_Cloud` و `saleemPOSDB` و `Shop1` و `Shop2` و `swstore` وقواعد النظام. `XtraPOS_Branch_03` و `XtraPOS_Branch_97` مقبولان كاسم مختار. `SelectedDatabaseProvisioner` ينشئ القاعدة فقط إذا كانت غائبة، بالتجميع `Arabic_CI_AS`، ولا يعمل `DROP`.

أخطاء SQL التي تُترجم إلى «تعذر الوصول إلى خادم البيانات من خادم التطبيق»: `-1` و `2` و `5` و `26` و `40` و `53` و `64` و `121` و `233` و `258` و `-2` و `10054` و `10060` و `10061` و `10065` و `11001` و `11004` و `40615`. الرمزان `262` و `15247` يعنيان أن الحساب لا يملك صلاحية إنشاء القاعدة.

## 6. معالج الإعداد ووكيل الفرع

الموقع المنشور يعمل على Linux. «على هذا الجهاز» يعني جهاز ويندوز الذي يفتح المستخدم منه الموقع، وليس خادم Linux.

المسار الذي يشغّل الوكيل فقط:

فرع مستقل، ثم «إنشاء قاعدة بيانات جديدة»، ثم «على هذا الجهاز».

لا يُشغَّل هذا المسار لخادم SQL بعيد، ولا لفرع بعيد مُعد سابقاً، ولا لقاعدة موجودة في مكان آخر.

الوكيل موجود في العملية الحالية ولا يُعاد بناؤه كمنتج ثانٍ. الملفات:

- `src/XtraPOS.Api/Setup/BranchAgentRegistry.cs`
- `src/XtraPOS.Api/Setup/BranchAgentWorker.cs`
- `src/XtraPOS.Api/Setup/BranchAgentExecutor.cs`
- `src/XtraPOS.Api/Setup/BranchAgentRelay.cs`
- `src/XtraPOS.Api/Setup/BranchAgentLocalEndpoint.cs`
- `src/XtraPOS.Api/Endpoints/BranchAgentEndpoints.cs`
- `src/XtraPOS.BranchAgent.Setup`
- `src/XtraPOS.Web/src/setup/branchAgentLocal.ts`
- `src/XtraPOS.Web/src/components/SetupWizard.tsx` داخل `NewLocalBranchDatabase` فقط

قواعد الأمان المبنية:

- رمز الربط 8 خانات ست عشرية، تُخزَّن تجزئة SHA-256، عمره 10 دقائق، ويُستخدم مرة واحدة.
- رمز الوكيل 32 بايتاً. الملف `App_Data/branch-agents.json` يحفظ التجزئة ومعرّف الجهاز والنسخ، ولا يحفظ الرمز ولا حمولة المهمة.
- المهمة لها nonce، عمرها 3 دقائق، تُطالب مرة واحدة، وإعادة الإبلاغ مرفوضة.
- العملية الوحيدة `provision-branch`.
- النسخة المقبولة `localhost` أو `localhost\NAME`. الاسم من 1 إلى 16 حرفاً إنجليزياً أو رقماً أو `_` أو `-`. `.` و `(local)` تُعاملان كمحلي. العناوين الرقمية والفاصلة والمنفذ مرفوضة.
- JSON الإعداد يحذف المفاتيح `sql` و `command` و `script` و `query` و `statement`، ويفرض مصادقة ويندوز، ويمسح مستخدم SQL وكلمة سره.
- سجل `App_Data/logs/branch-agent.log` بلا أسرار.
- الربط متاح والمعالج في حالة `NotConfigured`. بعد اكتمال الإعداد يلزم مستخدم داخل بملفات تعريف الارتباط.
- إكمال التجهيز البعيد لا يحفظ رمز الجهاز في متصفح المدير.

الاستماع المحلي، وهو ما يراه المتصفح على جهاز ويندوز:

- `http://127.0.0.1:47331/status`
- `POST http://127.0.0.1:47331/pair` بالجسم `{ code, server, confirmTransfer }`
- الأصل المسموح: `https://test-pos.sharbelfashion.com` و `http://127.0.0.1:5173` و `http://localhost:5173` و `http://127.0.0.1:5080`
- ترويسة `Access-Control-Allow-Private-Network: true` لـ Chrome
- نفس الموقع المربوط يعيد الحالة. موقع مختلف من دون `confirmTransfer` يعيد 409 ولا ينقل الربط بصمت.
- الخدمة تستطيع الإقلاع بـ `--server` فقط والانتظار حتى يصل الربط من المعالج. `--pairing-code` بقي للتوافق ولا يُطلب من المستخدم.

خدمة ويندوز التي ينشئها المثبّت:

- الاسم `XtraPOSBranchAgent`
- المجلد `C:\Program Files\XtraPOS\BranchAgent`
- التشغيل التلقائي، ثم تشغيل فوري
- الأمر: `XtraPOS.Api.exe --branch-agent --server https://test-pos.sharbelfashion.com`
- الهوية `LocalSystem`
- المثبّت يحاول `CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS` ثم `GRANT CREATE ANY DATABASE` فقط. لا يغيّر قواعد المستخدم الموجودة ولا `shop1`
- الرمز يُحفظ بـ DPAPI لغرض `XtraPOS.BranchAgent`. الربط يجب أن يتم من الخدمة نفسها عبر المنفذ المحلي
- الحزمة تعتمد على .NET 8 الموجود على الجهاز. المثبّت غير موقّع Authenticode، لذلك قد يظهر SmartScreen. قبل التثبيت يتحقق من SHA-256 المنشور
- التنزيل: https://test-pos.sharbelfashion.com/downloads/XtraPOS-BranchAgent-Setup.exe
- البصمة: https://test-pos.sharbelfashion.com/downloads/branch-agent.sha256
- الوصف: https://test-pos.sharbelfashion.com/downloads/branch-agent.json
- الحزمة: https://test-pos.sharbelfashion.com/downloads/branch-agent.zip

واجهة المعالج، من دون CSS جديد:

- غياب الخدمة أو توقفها: زر «تثبيت خدمة XtraPOS على هذا الجهاز». نفس المثبّت يعيد التشغيل.
- نسخة أقدم من `0.3.0`: زر «تحديث خدمة XtraPOS».
- غير مربوط: المعالج ينشئ رمز الربط ويرسله إلى المنفذ المحلي.
- مربوط بموقع آخر: زر «نقل الربط إلى هذا الموقع».
- جاهز: قائمة النسخ المحلية، ومنها `localhost\BARTENDER`، ثم اسم القاعدة الذي كتبه المستخدم.
- لا SQL: «لم يتم العثور على SQL Server على هذا الجهاز.»
- التقدم غير السري يُحفظ في `sessionStorage`. كلمات سر SQL ورموز الوكيل لا تُحفظ في المتصفح.

مسارات الخادم:

- `POST /api/setup/branch-agents/pairing-codes`
- `GET /api/setup/branch-agents`
- `POST /api/branch-agent/pair`
- `POST /api/branch-agent/heartbeat`
- `GET /api/branch-agent/jobs`
- `POST /api/branch-agent/jobs/{id}/events`

إذا حمل `POST /api/setup/create-branch` قيمة `branchAgentId`، فالخادم لا ينشئ القاعدة على Linux. يرسل مهمة للوكيل. الوكيل ينشئ القاعدة محلياً ويطبّق ترحيلات الفرع ويحفظ الاتصال التشغيلي بـ DPAPI في `App_Data/operational-branch.json`.

ما لم يُنفَّذ على جهاز المستخدم بعد النشر: تشغيل المثبّت بصلاحية المسؤول، وربط الخدمة فعلياً، وإنشاء قاعدة حقيقية على `localhost\BARTENDER`. الحالة المعلنة لهذا الجزء `PARTIAL`.

## 7. سلوك المنتج المنجز

الإعداد: `GET /api/setup/status` يعيد `NotConfigured` أو `Configuring` أو `Configured` أو `ConfiguredButAdminMissing` أو `DatabaseUnavailable` أو `Error`. إنشاء الفرع يطبّق الترحيلات وينشئ النشاط والفرع والمحطة والجهاز والمدير الأول. الانضمام إلى فرع موجود ظاهر ومحدود حسب مسار المعالج.

البيع: رأس `Sales` وبنود `SaleItems`. سلة نقاط البيع منفصلة عن البيع المرحّل. إغلاق الطباعة بعد بيع دين مرحّل يبقي البيع ويفرغ السلة.

مراجعة المبيعات: زر «المبيعات». صف لكل بند. التاريخ الافتراضي اليوم بتوقيت `Asia/Hebron`. حجم الصفحة 50. التصدير `sales-review.xlsx` بحد 5000 بند. التكلفة والربح لـ Admin و Manager أو مشرف أو `System.Administer` فقط.

الدين: يُسجل في دفتر الفرع أولاً. التحصيل من الرئيسية ثم الزبائن ثم الديون يمكن بلا وردية مفتوحة عبر `allowWithoutSession`. التحصيل من نقاط البيع ما زال يطلب وردية. الترحيل `041_OptionalCollectionSession` يجعل `BranchDebtCollections.CashSessionID` قابلاً للفراغ.

العضوية: Passkey والتحقق بالهاتف موجودان في المخطط والخدمات. أسرارهما في بيئة الخادم.

## 8. أين وصل النشر

الإصدار النشط على موقع الاختبار، وتم التأكد منه في 12 نوفمبر 2026:

`20261112-131021-2969d0d`

SHA المكتوب في الإصدار: `2969d0d81354af584ad37d3736075480419a4eda`

نتائج التحقق:

- `GET /health` أعاد `{"status":"Healthy","version":"0.3.0.0"}`
- ملف المثبّت متاح وحجمه 139776 بايت
- نص «تثبيت خدمة XtraPOS على هذا الجهاز» موجود في `assets/index-Dbhk2Qep.js`
- التفعيل قال إن `XtraPOS_Branch_Test_Staging` و `XtraPOS_Central_Test_Staging` غير متاحتين، ولم تُنشأ قاعدة ولم يُطبَّق ترحيل
- خدمة الإنتاج لم تُعد تشغيلها

إصدارات اختبار سابقة بقيت محفوظة لأن التفعيل يبدّل المؤشر ولا يحذف مجلد الإصدار السابق. منها `20261111-235822-2969d0d` و `20261111-234508-2969d0d` و `20261111-225159-2969d0d`.

حالة معالج الاختبار قبل إكمال إعداد حقيقي: `NotConfigured`. قائمة أجهزة الوكيل على الخادم كانت فارغة لأن لا جهاز ينبض.

## 9. ما يجب أن يفعله الطرف التالي

1. يفتح https://test-pos.sharbelfashion.com من جهاز ويندوز الذي عليه SQL Server.
2. يختار فرعاً مستقلاً ثم «إنشاء قاعدة بيانات جديدة» ثم «على هذا الجهاز».
3. إذا لم تكن الخدمة موجودة يضغط «تثبيت خدمة XtraPOS على هذا الجهاز»، يشغّل الملف، ويوافق على صلاحية ويندوز.
4. يرجع إلى المعالج. الكشف والربط يجب أن يكملا من الصفحة.
5. يختار `localhost` أو `localhost\BARTENDER`، ويبقي اسم القاعدة الذي كتبه، ثم يكمل النشاط والفرع ونقطة البيع والصندوق.
6. لا يشغّل الوكيل من Terminal أو PowerShell أو نافذة الخدمات.
7. لا ينشر إلى الإنتاج ولا يمرر `-WriteCentral` ولا ينشئ قاعدة اختبار على خادم SQL البعيد.

إذا رُفض المثبّت بسبب SmartScreen فالسبب أنه بلا توقيع Authenticode. لا يُستبدل ذلك بتعطيل فحص البصمة.

## 10. مراجع أقدم

- `XTRAPOS_PROJECT_REFERENCE_2026-11-10.md` مرجع البيئات حتى 10 نوفمبر، بلا أسرار.
- `طريقة-النشر-على-موقع-الاختبار.md` أمر النشر. رقم الإصدار المكتوب فيه أقدم من الإصدار الحالي.
- `SALES_REVIEW_AND_COLLECTIONS_STATUS_2026-11-10.md` تسلسل مراجعة المبيعات والتحصيل.
- `deploy/STAGING_CICD.md` حدود النشر عبر GitHub.
- `XTRAPOS_UI_STANDARD.md` معيار الواجهة.
- `AGENTS.md` حدود وكيل السحابة وقاعدة `XtraPOS_Branch_Test_Cloud`.
