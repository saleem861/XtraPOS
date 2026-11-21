# XtraPOS — ملف تسليم كامل

تاريخ هذا الملف: 20 نوفمبر 2026، الساعة 22:49 بتوقيت الخليل (`Asia/Hebron`). آخر تحديث: 21 نوفمبر 2026، 01:25.
المستودع: `https://github.com/saleem861/XtraPOS.git`.
رأس `main` عند كتابة الملف: `22ed48a`.
الغرض: يُقرأ هذا الملف كاملاً قبل أي تعديل. الأسماء الإنجليزية هنا هي أسماء الكود والجداول والمسارات. لا تُخترع جداول أو شاشات غير مذكورة. إذا تعارض الملف مع الكود الحالي على `main` فالكود هو المرجع، ويُحدَّث الملف.

لا تنسخ إلى هذا الملف كلمات سر أو مفاتيح `.ssh` أو محتوى `appsettings.json` أو `App_Data`.

---

## 1. ما هو النظام

XtraPOS نظام نقطة بيع يعمل بلا اتصال على جهاز الفرع، ويرحّل العمليات لاحقاً إلى قاعدة مركزية واحدة.

- الواجهة: React 19 و Vite 8 و TypeScript، في `src/XtraPOS.Web`. اللغة المعروضة عربية، والاتجاه من اليمين إلى اليسار.
- الخادم: ASP.NET Core 8، في `src/XtraPOS.Api`. يقدّم الواجهة من `wwwroot` وواجهات `/api`.
- قاعدة الفرع العاملة: SQL Server، الاسم المعتمد `XtraPOS_Branch` (`BranchDatabase.Name`).
- القاعدة المركزية العاملة: SQL Server، الاسم المعتمد `XtraPOS_Central` (`CentralDatabase.Name`).
- لا تُستخدم قاعدة وهمية. قواعد الاختبار تحمل `_Test_` في الاسم، مثل `XtraPOS_Branch_Test_Staging` و `XtraPOS_Central_Test_Staging` في `StagingSqlDefaults`. لا تُنفَّذ عليها ترحيلات الإنتاج عبر `SchemaPropagationService` لأن `OperationalDatabaseGuard.IsProductionCatalog` يرفضها.
- مسار التطوير على جهاز ويندوز: `D:\Google\XtraPOS`.
- مسار التركيب على جهاز الكاشير: `C:\Program Files\XtraPOS\BranchAgent`.
- خدمة ويندوز: `XtraPOSBranchAgent`. العملية: `XtraPOS.Api.exe`.
- منفذ التطبيق المحلي: `47335` (`BranchAgentHostCommand.LocalAppPort`). المتصفح يفتح `http://127.0.0.1:47335`.
- التحديث و`/api/device/local-registration` مسموحان من `loopback` فقط.

المشاريع الأخرى:

- `src/XtraPOS.BranchAgent.Setup`: مثبّت جهاز الفرع.
- `tools/XtraPOS.DeviceAgent`: أداة جهاز.
- `tests/XtraPOS.Api.Tests`: اختبارات الخادم. اختبارات الواجهة بجانب المكوّنات (`*.test.tsx`).

---

## 2. شكل التشغيل

جهاز الفرع يشغّل `XtraPOS.Api` كخدمة. الخدمة تقرأ SQL محلياً، وتفتح الاتصال المركزي من ملف في `App_Data` (لا يُستبدل عند التحديث). الواجهة كلها على الجهاز نفسه. لا يوجد خادم سحابة للبيع.

عند الإقلاع يطبّق `MigrationRunner.ApplyPendingAsync` ترحيلات الفرع الناقصة، و`CentralMigrationRunner` ترحيلات المركز إذا كان الاتصال المركزي محفوظاً. كل ترحيل يُسجَّل في `dbo.DatabaseSchemaHistory` بالمفتاح `MigrationID`. التشغيل المتكرر آمن: السكربتات تستخدم `IF OBJECT_ID ... IS NULL` و`IF NOT EXISTS`.

`SchemaPropagationWorker` يمر على قواعد الفروع الإنتاجية المرتبطة ويطبّق ترحيلات الفرع الناقصة، ويسجّل النتيجة في `dbo.SchemaUpdateHistory`.

---

## 3. الإعداد الأول

المراحل في `App.tsx`: تحميل، خطأ، جهاز غير مسجّل، اختيار الإعداد، معالج الإعداد، شريط التقدم، شاشة التسليم (`BrandHandoff`)، إنشاء المدير، تسجيل الدخول، تغيير كلمة المرور، ثم الرئيسية.

خطوات شريط التقدم (`SETUP_STEPS` في `src/XtraPOS.Web/src/types/setup.ts`):

1. `checkServer` فحص خادم قاعدة البيانات
2. `createDatabase` إنشاء قاعدة بيانات XtraPOS
3. `createTables` إنشاء الجداول
4. `createBusiness` إنشاء المنشأة
5. `createBranch` إنشاء الفرع
6. `createStation` إنشاء نقطة البيع
7. `registerDevice` تسجيل الجهاز
8. `initialSync` مزامنة المستخدمين من المركزية
9. `createAdmin` التحقق من حساب المدير
10. `verify` التحقق من سلامة الإعداد

`operatingMode` في مسودة الإعداد: `Standalone` أو `CentralManagement`.

مسار جهاز جديد: حزمة `branch-agent.zip` على موقع الإعداد، وفيها `appsettings.json`. مسار جهاز مركّب مسبقاً: زر «تحديث التطبيق» ينزّل `XtraPOS-BranchAgent.zip` من آخر GitHub Release، وهذه الحزمة بلا `appsettings*.json` وبلا `App_Data`.

---

## 4. الشاشات

لا يوجد موجه عناوين لكل شاشة. `App.tsx` يبدّل المرحلة، و`HomeScreen` يبدّل الشاشة الداخلية بالحالة.

### 4.1 قبل الدخول

| الشاشة | الملف | ماذا تفعل |
|---|---|---|
| الاختيار | `SetupChoiceScreen.tsx` | بدء إعداد جديد أو الانضمام لنقطة موجودة |
| المعالج | `SetupWizard.tsx` | جمع المنشأة والفرع والنقطة والجهاز وحساب المدير والاتصال المركزي |
| التقدم | `SetupProgressScreen.tsx` | يعرض خطوات `SETUP_STEPS` |
| التسليم | `BrandHandoff.tsx` | بعد نجاح الإعداد، قبل فتح الدخول |
| جهاز غير مسجّل | `UnregisteredDeviceScreen.tsx` | النية `join` أو `independent` أو `central` |
| إنشاء المدير | `AdminSetupScreen.tsx` | عندما الحالة `ConfiguredButAdminMissing` |
| الدخول | `LoginScreen.tsx` | اسم المستخدم وكلمة المرور. زر «تحديث التطبيق» يرسل `POST /api/local-update` |
| تغيير كلمة المرور | `PasswordChangeScreen.tsx` | إذا `mustChangePassword` |
| الحالة | `StatusScreen.tsx` | قاعدة غير متاحة أو خطأ إعداد |

زر التحديث يعرض مؤقت 90 ثانية (`UPDATE_SECONDS`). يعيد تحميل الصفحة فقط إذا `/health` سقط ثم عاد. رأس الشاشة يعرض `VITE_RELEASE_SERIAL` الذي يُخبَز عند بناء الواجهة.

### 4.2 الرئيسية `HomeScreen.tsx`

تعرض المنشأة والفرع والكاونتر والجهاز والمستخدم والأدوار.

بطاقات القائمة:

- «نقطة البيع» إذا `Items.View` أو `System.Administer`.
- «الأصناف» بنفس الشرط.
- «مراجعة الجرد» إذا `CashDrawer.Review` أو الدور `Supervisor` أو `Admin`. النص الصغير: «عمليات جرار كل الفروع».
- «الزبائن» للجميع، والبطاقات الداخلية تُعطَّل حسب الصلاحية.
- «الإعدادات» للمدير أو من يرى المستخدمين أو يضبط فئات النقد.

### 4.3 الإعدادات

- «إدارة المستخدمين» → `UserManagementScreen.tsx`. الصلاحية `Users.View` أو المدير.
- «فئات النقد» → `CashDenominationScreen.tsx`. الصلاحية `CashDrawer.Configure` أو مشرف أو مدير.
- «سجل الترحيل» → `OperationLogScreen.tsx`. للمدير. يقرأ `GET /api/operations/log`، ويعيد المحاولة `POST /api/operations/log/{id}/retry`، ويحذف `DELETE`.
- «الربط المركزي» → `CentralSetupScreen.tsx`. للمدير.
- «إعادة تهيئة النقطة»: `POST /api/device/reset` مع `deregisterCentral: true`. قاعدة الفرع تبقى إلا إذا اختار «حذف قاعدة البيانات المحلية». الخدمة تتوقف وملفات التسجيل تُحذف. البيانات المحلية لا تُحذف افتراضياً.

### 4.4 نقطة البيع `PosScreen.tsx`

العنوان «نقطة البيع». الشريط يعرض رقم الزبون واسمه والنقطة واسم الموظف.

أزرار أسفل الشاشة:

- «رمز العضوية»: يفتح عضوية الزبون (`MembershipQr` وصفحات العضوية).
- «حفظ»: يحفظ المسودة محلياً.
- «طباعة ومشاركة»: معاينة الفاتورة `SaleA4Sheet.tsx`.
- «المحفوظات»: `HeldDraftsScreen.tsx` بعنوان «استرجاع المسودات المحفوظة».
- بيع آجل: `CreditSaleScreen.tsx` إذا كان الآجل مسموحاً.
- «تسديد»: `PosCollectionScreen.tsx` إذا كان التحصيل مسموحاً.
- مفتاحا `CreditSales:Enabled` و`CreditCollections:Enabled` مفعّلان افتراضياً على جهاز الفرع (`BranchAgentFeatureDefaults`، أدنى أولوية في الإعداد)، ويطفئهما `appsettings.json` على الجهاز إن وُضعا `false`. `CreditReversals:Enabled` يبقى مطفأً ما لم يُفعَّل صراحة.
- «شاشة»: يفتح شاشة الزبون `/customer-display` (`CustomerDisplayPage.tsx`) في نافذة على الشاشة الثانية إن سمح Chrome بإدارة النوافذ. نقطة البيع تبث الحالة عبر `BroadcastChannel` باسم `xtrapos-customer-display` من `buildDisplayState` (`src/customerDisplay/customerDisplay.ts`): `idle` ترحيب ورمز عضوية العداد، `sale` الأصناف وآخر صنف والخصم والمطلوب، `payment` المطلوب والمدفوع والباقي، `deferred` المبلغ المؤجَّل فقط، `thanks` لمدة 20 ثانية مع رمز الفاتورة إن وُجد `VITE_PUBLIC_INVOICE_BASE` وإلا رمز العضوية. لا تُبث التكلفة ولا الربح ولا رصيد الدين ولا الحد الائتماني. رمز التحقق من العضوية يظهر فوقها من قناة `xtrapos-membership-display`.
- «الجرد»: الزر موجود ومعطّل (`disabled`). جرد الجرار يُفتح من زر «الجرار» داخل نقطة البيع عبر `CashDrawerScreen.tsx`.
- استعلام المبيعات: `SalesInquiryScreen.tsx` ثم تفاصيل `OrderDetailsScreen.tsx`.

لوحات الإدخال: `FullKeyboard.tsx` و`PosNumberPad.tsx` و`NumericKeypad.tsx` و`NumericEditModal.tsx`. في شاشات الجرد ارتفاع لوحة المفاتيح 33٪ من الشاشة (صنف `full-keyboard-screen-keyboard`). Enter في الحقول ينقل التركيز للحقل التالي، ما عدا داخل `.pos-screen` خارج شاشة الدين.

البيع يُحفظ محلياً أولاً في `SalePostingService` داخل جداول `Sales` و`SaleItems` و`SalePayments` وحركة المخزون. بعد الحفظ يُضاف صف في `BranchOperationLog` من النوع `CompletedSale` عبر `SaleCentralRelay.EnqueueCompletedAsync`. البيع يعتبر ناجحاً على الفرع حتى لو كان المركز غير متصل.

### 4.5 جرد الجرار `CashDrawerScreen.tsx`

- الفئات من `CashDenominations`. البذور: 0.5، 1، 2، 5، 10، 20، 50، 100، 200. الفئة شركة كاملة: لا فرع ولا نقطة بيع. يمكن إضافة فئة من شاشة «فئات النقد».
- الشبكة ثلاثة أعمدة. عدد الصفوف `ceil(عدد الفئات / 3)`. كل الخانات تبقى فوق لوحة المفاتيح.
- العد الأول ثم، إذا اختلف المجموع عن الرصيد المتوقع، سطر واحد فقط: «المجموع المعدود لا يطابق الرصيد المتوقع. أعد العد.» ثم صندوق العد الثاني. لا تُكرَّر الجملة في رسالة الحالة.
- أثناء الحفظ تظهر «جاري حفظ العد» مع مؤشر انتظار.
- الفرق = المعدود − المتوقع (`CashDrawerRules.Difference`). الحالة `Matched` أو `Shortage` أو `Surplus`.
- النقص يُسجَّل على الموظف (`ChargesEmployee`). أي فرق غير الصفر يحتاج تبريراً.
- كل تأكيد صف جديد. العد لا يُحدَّث بعد الحفظ. إعادة فتح الجرار تبدأ من العد الأول، والجرود السابقة تبقى.
- فهرس `UX_CashDrawerCounts_OpenAttempt` أُسقط في ترحيل الفرع `048` والمركز `034` حتى يُحفظ عد لاحق في الوردية نفسها.
- كل تأكيد يُسجَّل أيضاً في `BranchOperationLog` بنوع `CashDrawerCount`.

### 4.6 مراجعة الجرد `CashDrawerReviewScreen.tsx`

- المدير والمشرف يريان كل الفروع. الكاشير لا يفتح الشاشة.
- قائمتا الفرع والنقطة لا تكرران الاسم. التصفية في `CashDrawerService` تشمل كل الصفوف التي تحمل الاسم نفسه.
- قائمة العمليات عمودية مختصرة. فتح عملية يعرض شاشة عرضية `.drawer-view`:
  - يمين 30٪: الكاشير، الفرع، النقطة، الوقت، مجموع العد الأول، مجموع العد الثاني، الفرق ولونه، التبرير، قرار المشرف.
  - يسار: شبكة فئات العد الثاني، وزر «فئات العد الأول» / «فئات العد الثاني».
  - أسفل: لوحة المفاتيح والأرقام بارتفاع 33٪.
- القرار على العد الثاني فقط: `KeepShortage` أو `KeepSurplus` أو `Waive`. يُحفظ صفاً في `CashDrawerDecisions` ولا يغيّر أرقام العد. نوع سجل الترحيل `CashDrawerDecision`.

### 4.7 الأصناف

- `ItemCatalogScreen.tsx` قائمة الأصناف.
- `ItemDetailScreen.tsx` بطاقة الصنف والوحدات والأسعار والمخزون والحركات.
- الواجهة: `ItemEndpoints` تحت مجموعة الأصناف. التعديل يحتاج صلاحيات التعديل من ترحيل `011`.
- سعر افتراضي وسعر فرع. التفرد في ترحيل `045` على الفرع و`031` على المركز.
- تكلفة المستودع واستلام المستودع: `WarehouseCostService` ونقاط `warehouse-cost` و`warehouse-receipt`.
- ربح سطر البيع: `UnitProfit` وكمية الربح من ترحيل `046` / المركز `032`.

### 4.8 الزبائن `CustomersMenuScreen.tsx`

العنوان «إدارة الزبائن». البطاقات:

- «إضافة زبون جديد» → `NewCustomerScreen.tsx`. يحتاج `Customers.Create` أو `Customers.Manage` أو `System.Administer`.
- «استعراض الزبائن» → `CustomerBrowseScreen.tsx`. يحتاج رؤية أو إنشاء.
- «مشتريات الزبون»: البطاقة ظاهرة ومعطّلة في القائمة (`active` لا يشملها).
- «ديون الزبائن» → بحث ثم `PosCollectionScreen.tsx`. يحتاج رؤية أو إنشاء.

من بطاقة الزبون أيضاً: أسعار خاصة `ExceptionalPricesScreen.tsx`، عضوية `CustomerMembershipSection.tsx`، مفتاح مرور `PasskeyQrPanel.tsx` وصفحة الهاتف `PasskeyPhonePage.tsx`.

التحقق من الهاتف عبر مزوّد (Twilio في الترحيلات `021` و`022`). لا تُعرض أسرار المزوّد في الواجهة.

### 4.9 العضوية

صفحات الهاتف والمتصفح: `MembershipPhonePage.tsx` و`MembershipEnrollPage.tsx` و`MembershipDisplayPage.tsx` و`MembershipReadyPage.tsx` و`MyLogPage.tsx`.
المسار يبدأ من Google أو Apple (`/api/membership/auth/...`) ثم إثبات وربط. على جهاز الفرع يُنشر رمز العداد عبر وكيل الفرع (`/api/branch-agent/membership/...`) لأن هاتف الزبون لا يصل إلى `127.0.0.1`.

### 4.10 المستخدمون والصلاحيات

الأدوار بعد `003_NormalizeSystemRoles`:

- `Admin` / «مدير النظام». كان موجوداً في `002`.
- `Supervisor`. حل محل `Manager` إن وُجد.
- `User`. حل محل `Cashier` إن وُجد. اسم العرض القديم في `002` كان «كاشير».

صلاحيات الجرد:

- `CashDrawer.Count` للمدير والمشرف والمستخدم.
- `CashDrawer.Review` و`CashDrawer.Configure` للمدير والمشرف فقط.

صلاحيات أخرى مذكورة في الشاشات: `System.Administer` و`Items.View` و`Users.View` و`Customers.View` و`Customers.Create` و`Customers.Manage`. بقية الصلاحيات مزروعة في ترحيلات `002` و`008` و`011` و`024` و`029` و`030`.

---

## 5. قاعدتا البيانات

كلا القاعدتين SQL Server. كثير من الجداول صار موجوداً على الجهتين عبر ترحيل التوحيد: الفرع `043_UnifiedSchemaObjects` و`044_UnifiedSchemaChecks`، والمركز `029` و`030`. هذا يجعل الشكل واحداً. مصدر البيانات ليس واحداً:

- بيانات الشركة والفروع والأصناف والأسعار والزبائن وسياسة الدين: المركز هو المصدر، والفرع نسخة عمل.
- المبيعات والمدفوعات وحركات مخزون الفرع وجرد الجرار: الفرع هو مصدر العملية، والمركز يستلم نسخة عبر سجل الترحيل.
- `BranchRemoteSync.IsImplemented = false`. لا توجد مزامنة إلى «نسخة فرع بعيدة». الاتجاه الطبيعي المخطط `BranchLocalToBranchRemote`، والعكس `RestoreOnly` للطوارئ فقط. كلا الدالتين ترميان `InvalidOperationException`.

### 5.1 ترحيلات المخطط (schema migrations)

تُنفَّذ بالترتيب الرقمي. الملف في `src/XtraPOS.Api/Data/Migrations` للفرع و`src/XtraPOS.Api/Data/Central/Migrations` للمركز. السجل `dbo.DatabaseSchemaHistory`: `MigrationID`، `MigrationName`، `ApplicationVersion`، `AppliedAt`.

آخر ترحيل فرع: `048_CashDrawerOpenCount` «Allow another cash drawer count after the first».
آخر ترحيل مركز: `034_CashDrawerOpenCount` بالاسم نفسه.

| فرع | موضوعه | مركز مقابل |
|---|---|---|
| 001 | منشأة، فروع، نقاط، أجهزة، إعدادات | 001 أساس المركز |
| 002 | مستخدمون وأدوار وصلاحيات | ضمن 001 ثم 004 |
| 003 | توحيد الأدوار إلى Admin وSupervisor وUser | — |
| 004–008 | أصناف ووحدات وأسعار ومخزون وصلاحية العرض | 002 ملكية الصنف |
| 009 | سجل الاستيراد | ضمن 001 |
| 010 | طابور المزامنة | — |
| 011–014 | تعديل صنف، معدل بيع، إدارة مستخدمين، مصدر الحركة | 003 تفرد رمز الفرع، 004 إدارة المستخدمين |
| 015 | الزبائن | 005 |
| 016–018 | المبيعات والمدفوعات وتجاوز السعر | — |
| 019 | مفاتيح البحث العربية | 006 |
| 020–023 | تحقق الهاتف والأسعار الخاصة والكمية | 007–010 |
| 024–028 | صلاحيات الدين وطوابير البيع الآجل والتحصيل والعكس | 011–014 |
| 029–030 | إدارة الزبون للمشرف وإنشاء زبون | — |
| 031–033 | مفاتيح المرور وإعداد الجهاز | — |
| 034–036 | بيانات الزبون المرجعية واستثناء العضوية | 016–023 |
| 037–041 | سياسة دين الفرع وصندوق الوارد وسجل العمليات ودفتر الدين | 026 أحداث مالية |
| 042 | صناديق و`SchemaUpdateHistory` | 028 |
| 043–045 | توحيد الكائنات وفحوصها وتفرد السعر | 029–031 |
| 046 | ربح سطر البيع | 032 |
| 047–048 | جرد الجرار ثم السماح بعد لاحق | 033–034 |

المركز أيضاً: `015` سجل نقاط البيع و`BranchSqlEndpoints`، `018` بيانات مرجعية لبيئة الاختبار، `019`–`025` العضوية ورمز العداد اليومي، `027` سجل نقاط المزامنة.

### 5.2 الجداول

كل `CREATE TABLE` يستخدم `dbo`. المفتاح غالباً `uniqueidentifier`. المبالغ `decimal(19, 4)`. الوقت `datetime2` بقيمة `SYSUTCDATETIME()`.

الهوية والتنظيم: `Businesses`، `Branches`، `POSStations`، `Devices`، `POSDeviceConfigurations`، `Cashboxes`، `Users`، `Roles`، `Permissions`، `UserRoles`، `RolePermissions`، `UserBranchAccess`، `UserLoginAudit`، `UserAdministrationAudit`، `SystemSettings`، `DatabaseSchemaHistory`، `SchemaUpdateHistory`، `DataImportHistory`.

الأصناف والمخزون: `Items`، `ItemUnits`، `PriceTypes`، `ItemPrices`، `ItemPriceHistory`، `ItemBranchInventory`، `BranchStockBalances`، `StockMovementTypes`، `StockMovements`.

الزبائن والدين: `CustomerClassifications`، `Customers`، `CustomerAdministrationAudit`، `CustomerPhoneVerificationChallenges`، `CustomerItemSpecialPrices`، `CustomerCreditPolicies`، `CustomerReceivables`، `CustomerCreditLedger`، `CreditPaymentAllocations`، `CreditAuthorizations`، `CreditAuditLog`، `CreditCollectionIntents`، `CreditCollectionReservations`، `CreditRepaymentReversals`، `CreditAllocationReversals`.

المبيعات: `Sales`، `SaleItems`، `SalePayments`، `POSCashSessions`.

العضوية ومفتاح المرور: `CustomerMembershipIdentities`، `CustomerMembershipSessions`، `MembershipVerificationRequests`، `MembershipVerificationAudit`، `MembershipEnrollmentRequests`، `MembershipAuthAttempts`، `MembershipCounterCodes`، `CustomerPasskeys`، `CustomerPasskeySessions`، `CustomerPasskeyAudit`.

المزامنة والترحيل: `SyncEndpoints`، `SyncCheckpoints`، `SyncInbox`، `SyncOutbox`، `SyncChangeLog`، `SyncEntityOwnership`، `BranchOperationLog`، `BranchFinancialOutbox`، `BranchFinancialInbox`، `FinancialSyncCheckpoints`، `BranchDebtCollections`، `BranchCollectionOutbox`، `BranchCollectionReversals`، `BranchReversalOutbox`، `CentralFinancialEvents`، `CentralPosRegistry`، `CentralPosReplica`، `BranchSqlEndpoints`.

جرد الجرار، على الفرع والمركز:

- `CashDenominations`: `DenominationID`، `UnitValue` فريد وأكبر من صفر، `DisplayName`، `SortOrder`، `IsActive`.
- `CashDrawerCounts`: `CountID`، `BusinessID`، `BranchID`، `POSStationID`، `DeviceID`، `CountedByUserID`، `CashSessionID`، `ParentCountID`، `AttemptNumber`، `CountedAmount`، `ExpectedAmount`، `DifferenceAmount`، `Status`، تبرير الكاشير. الصف لا يُحدَّث بعد الإدخال.
- `CashDrawerCountLines`: كمية كل فئة داخل العد.
- `EmployeeCashErrors`: النقص المحمول على الموظف.
- `CashDrawerDecisions`: قرار المشرف، صف جديد، لا يغيّر العد.

أعمدة بقية الجداول في ملف الترحيل الذي أنشأها. لا تُلخَّص من الذاكرة إذا كان التعديل يمس عموداً.

`SystemSettings.SettingKey = DatabaseRole` قيمته `Branch` على قاعدة الفرع.

### 5.3 اتصال SQL

`SqlConnectionFactory` يبني اتصال الفرع من الإعداد. اتصال المركز من `SyncEndpointStore` / ملف `App_Data`. `SyncEndpoints` يحفظ الدور `Central` أو `BranchRemote`، والمكان `Local` أو `Remote`، واسم الخادم والمنفذ وقاعدة البيانات ونوع المصادقة. كلمة المرور المخزنة `ProtectedPassword`. لا تُطبع.

---

## 6. طرق الترحيل (نقل البيانات)

هذه غير ترحيلات المخطط. المخطط يغيّر الجداول. الترحيل هنا ينقل الصفوف بين الفرع والمركز.

### 6.1 من المركز إلى الفرع

`SharedDataSync` والتيار `CentralMaster`.

- `BootstrapAsync`: أول نسخة. تنسخ المنشأة والفروع والأصناف والوحدات والأسعار والزبائن والذمم ودفتر الدين إلى الفرع. إذا كانت القاعدتان على نفس المثيل تستخدم اسماً ثلاثياً `[XtraPOS_Central].dbo`. إذا كانتا على خادمين مختلفين تُحمَّل الجداول إلى جداول مؤقتة ثم تُدمج. المهلة 600 ثانية. بعدها `AccountAccessRepair`.
- `PromoteAsync`: ترقية بيانات الفرع نحو المركز. المهلة 300 ثانية.
- حالات النسخة: `NotInitialized` و`Bootstrapping` و`Ready` و`Stale` تحت المفتاح `CustomerReplicaState`.
- `CustomerReplicaSyncWorker`: حلقة خلفية تُبقي نسخة الزبائن حديثة.
- `CentralPosReplicaSync`: نسخة سجل نقاط البيع إلى `CentralPosReplica`. تُستدعى أيضاً من `POST /api/sync/pos-replica`.
- واجهات المركز في `CentralEndpoints` تحت مجموعة المركز: `status` و`create` و`setup` و`test` و`promote` و`link` و`bootstrap` و`sync` و`refresh-prices` و`branch-remote`.

حذف صف من المركز لا يُعاد تنفيذه هنا كحذف أعمى على الفرع. الدمج يعتمد المفاتيح وعدم وجود الصف.

### 6.2 من الفرع إلى المركز

السجل الواحد: `dbo.BranchOperationLog`. `CreditOutboxWorker` يقرأ رأس الطابور كل نحو 30 ثانية ويرحّل نوعاً واحداً في كل دورة:

| `OperationType` | المعالج |
|---|---|
| `CreditSale` | `CreditOutboxProcessor` |
| `DebtCollection` | `CreditCollectionService.RecoverOnceAsync` |
| `CollectionReversal` | `CreditReversalService.RecoverOnceAsync` |
| `CustomerUpsert` | `BranchCardOutboxSync.RelayCustomerAsync` |
| `MembershipEnrollment` أو `MembershipUpsert` | `MembershipReplica.RelayAsync` |
| `CompletedSale` | `SaleCentralRelay.RelayAsync` |
| `CashDrawerCount` أو `CashDrawerDecision` | `CashDrawerService.RelayLoggedAsync` |

إذا لم يوجد رأس، يجرب البيع الآجل ثم التحصيل ثم العكس. عند غياب اتصال المركز تنتظر الدورة دقيقة. `BranchOperationLog.PurgeAsync` ينظّف السجل القديم في بداية الدورة.

الجداول المساعدة ما زالت موجودة ولا تُلغى: `BranchFinancialOutbox` و`BranchCollectionOutbox` و`BranchReversalOutbox` و`BranchFinancialInbox`. الصندوق الوارد `FinancialInboxWorker` و`FinancialInboxSync` يسحب الأحداث المالية من المركز إلى الفرع.

البيع الآجل لا يُعتبر مقبولاً على المركز إلا بعد نجاح الترحيل. البيع النقدي محفوظ على الفرع فوراً، ونسخته المركزية لاحقة.

### 6.3 ما لا يُفعل

- لا مزامنة `BranchRemote`.
- لا حذف لقاعدة `XtraPOS_Branch` أو `XtraPOS_Central` ضمن النشر أو التحديث.
- تحديث التطبيق يستثني `App_Data` و`appsettings*.json`.
- إعادة تهيئة الجهاز لا تحذف القاعدة إلا بخيار صريح.

---

## 7. واجهات مهمة

الإعداد والدخول: `/api/setup/status`، `create-branch`، `create-admin`، `test-connection`، `central-databases`، `branch-databases`، `branch-stations`، `central-admins`، `verify-central-admin`، `branch-code`، `branches`، `/api/auth/login`، `logout`، `change-password`، `me`، `/api/device/current`، `heartbeat`، `reset`.

المزامنة: `/api/sync/status`، `/api/central/connection`، `/api/sync/pos-replica`.

الجرد: `/api/cash-drawer/context`، `counts`، `denominations`، `reviews`، `reviews/{countId}`، `reviews/{countId}/decision`.

المبيعات: `/api/pos/sales-inquiry` و`export` و`{saleId}`.

الأصناف والزبائن والدين والعضوية ومفتاح المرور موثّقة في ملفات `src/XtraPOS.Api/Endpoints`. وكيل الفرع في `BranchAgentEndpoints.cs`: رموز الاقتران، النبض، المهام، ونشر العضوية.

التحديث: برنامج تحديث مستقل عن الخدمة، حتى لا يبقى خلل في كود التحديث عالقاً داخل النسخة المركّبة.

- السكربت `deploy/updater/xtrapos-updater.ps1` يعمل كمهمة مجدولة `XtraPOSUpdater` بحساب SYSTEM، عند الإقلاع وكل دقيقة (`IgnoreNew`)، من `C:\ProgramData\XtraPOS\Updater`. يكتب `heartbeat.txt` كل ثانيتين.
- يُثبَّت بـ `deploy/updater/install-updater.ps1` كمدير. يستخدم `schtasks /XML` لا `Register-ScheduledTask`، لأن WMI معطوب على بعض الأجهزة. برنامج الإعداد `XtraPOS.BranchAgent.Setup` يشغّله تلقائياً من مجلد `Updater` داخل الحزمة.
- `POST /api/local-update` (loopback فقط): إذا كان النبض أحدث من 3 دقائق يكتب `request.json` ويعيد `mode = updater` و`requestId`. غير ذلك يرجع إلى المسار القديم (`PublishedRelease.StartUpdater`، `mode = legacy`).
- `GET /api/local-update/status` يعيد `installed` من `release.json` في مجلد البرنامج، و`updaterAlive`، و`status` من `status.json`.
- المراحل: `Checking`، `Downloading`، `Backup`، `Stopping`، `Copying`، `Starting`، ثم `Succeeded` أو `UpToDate` أو `Failed`. عند الفشل بعد الإيقاف: `RollingBack` يعيد الملفات من النسخة الاحتياطية ويشغّل الخدمة.
- لا يُنسخ `App_Data` ولا `appsettings*.json` ولا مجلد `Updater`. السكربت يستبدل نفسه إذا تغيّر في الحزمة.
- شاشة الدخول تتابع `status.json` حتى 300 ثانية وتعرض المرحلة أو سبب الفشل.
- سجل البرنامج: `C:\ProgramData\XtraPOS\Updater\updater.log`. السجل القديم `C:\Windows\Temp\xtrapos-release-update.log` للمسار القديم فقط.
- ملفا السكربت ASCII فقط، لأن Windows PowerShell 5.1 يقرأ UTF-8 بلا BOM خطأ.

سبب التغيير (21 نوفمبر 2026): الخدمة كانت تكتب السكربت ثم يفشل `schtasks /Create` من داخلها، فلا تُنشأ المهمة ولا يحدث شيء منذ 18 نوفمبر.

---

## 8. النشر

السكربت: `scripts/publish-branch-release.ps1`. يُشغَّل على جهاز ويندوز الذي يملك `gh` ومفتاح SSH لموقع الإعداد.

يبني الواجهة برقم `VITE_RELEASE_SERIAL` من الساعة المحلية `yyyyMMdd-HHmmss`، وينشر الخادم إلى `artifacts\branch-agent-release-<serial>` (مجلد جديد في كل مرة منذ `22ed48a`، حتى لا يقف النشر على `appsettings.json` مقفل).

يضيف إلى مجلد النشر `release.json` (الرقم) ومجلد `Updater` (سكربتا برنامج التحديث)، ويبني `XtraPOS-BranchAgent-Setup.exe` ويرفعه مع حزمة الجهاز الجديد.

ينتج حزمتين:

- `artifacts\XtraPOS-BranchAgent.zip` بلا إعدادات، وتُرفع إلى GitHub Release باسم `branch-agent-<serial>` لزر التحديث.
- `artifacts\branch-agent.zip` مع `appsettings.json`، ومعها `branch-agent.sha256` و`branch-agent.json`، وتُرفع إلى موقع إعداد الجهاز الجديد.

نجاح النشر جملتان: `Published branch-agent-...` ثم `New device package updated`. إذا فشلت الثانية فزر التحديث قد يكون نُشر بينما موقع الجهاز الجديد بقي على الحزمة السابقة.

أمر النشر بعد السحب:

```text
Set-Location D:\Google\XtraPOS
git -c gc.auto=0 pull
powershell -ExecutionPolicy Bypass -File .\scripts\publish-branch-release.ps1
```

إذا سأل Git عن حذف كائنات غير مرجعية فالجواب `n`. إذا ظهرت `did not send all necessary objects` فالسحب ناقص ولا يُنشر.

---

## 9. حالة معروفة عند كتابة الملف

آخر تحديث: 21 نوفمبر 2026 قرابة 01:25 بتوقيت الخليل. مشاهدة من الجلسة، تحقق على الجهاز قبل الاعتماد عليها.

- آخر إصدار منشور: `branch-agent-20261121-011706` على GitHub وموقع الإعداد، ومعه `XtraPOS-BranchAgent-Setup.exe` الجديد.
- جهاز Labtop: برنامج التحديث `XtraPOSUpdater` مثبّت ويعمل، والزر حدّث الجهاز بنجاح عدة مرات (المراحل «1 من 6» حتى «6 من 6»).
- أي جهاز آخر برقم أقدم من `20261121-001534` لا يملك برنامج التحديث: يحتاج تركيباً يدوياً لمرة واحدة ثم `deploy/updater/install-updater.ps1` كمدير.
- WMI معطوب على Labtop (`Get-CimInstance` يعيد `Invalid class`). لا يؤثر على التحديث لأنه يستخدم `schtasks /XML`.
- الخدمة توقفت مرة وحدها حوالي 00:54 دون أثر في سجل أحداث ويندوز. أُعيد تشغيلها يدوياً. السبب غير معروف.
- على Labtop كانت نسختان `(1).cs` من مزامنة Google Drive في `tests` (نُقلتا إلى `D:\XtraPOS-duplicates`). المشروع داخل `D:\Google` فقد تتكرر.

أعمال مفتوحة:

- **للغد (22 نوفمبر):** قائمة «النقطة» في شاشة القيم الأولية على Labtop (إصدار `20261121-193456`) أظهرت «Labtop · صندوق 1 (1)» و«جميع النقاط» فقط. لم تُفحص بعد: هل في `CentralPosRegistry` على المركز نقاط أخرى بحالة `Active` ولها `DeviceID`؟ وهل ظهرت ملاحظة «تعذر الوصول إلى الإدارة المركزية»؟ القائمة تأتي من `PosDeviceSettingsService.ListPointsAsync` ← `CentralPointListAsync`. إزالة جدول سجل الترحيل من الشاشة تمت في الكود (`DeviceInitialValuesScreen.tsx`) ولم تُنشر بعد.

0. «القيم الأولية» (`docs/pos-device-settings.md`): جدول `PosDeviceSettings` على كل فرع يطابق `CentralPosDeviceSettings` لكل النقاط. الحفظ يدخل سجل الترحيل الموجود بنوع `DeviceSettings`، والسحب من `SyncChangeLog`. ترحيل المركز `035` مسجّل بموافقة المالك (21 نوفمبر)، ويُطبَّق تلقائياً عند أول دورة لأي جهاز فرع بعد النشر، فتؤخذ نسخة احتياطية من `XtraPOS_Central` قبل النشر.

1. صفحة فاتورة عامة على موقع الإعداد (عبر وكيل الفرع)، حتى يعمل رمز «افتح فاتورتك» في شاشة الزبون وزر مشاركة واتساب. حالياً `VITE_PUBLIC_INVOICE_BASE` غير مضبوط ويظهر رمز العضوية مكانه.
2. إعادة تشغيل الخدمة تلقائياً عند توقفها (`sc failure` في برنامج الإعداد وبرنامج التحديث).
3. زر إخفاء لوحة المفاتيح في الشاشات الست التي لا تملكه، بانتظار قرار: ظاهرة أم مخفية افتراضياً، وهل يُتذكر الاختيار.
4. طبقة Shift في `FullKeyboard` فيها حروف مكررة، ولا توجد طبقة إنجليزية رغم زر «اللغة».
5. ترقية `System.Formats.Cbor` 8.0.0 (ثغرة متوسطة NU1902).
6. `CreditReversals:Enabled` مطفأ عمداً.
7. بيانات دخول SQL المركزية أُرسلت في المحادثة بنص واضح: نُصح المستخدم بتغيير كلمة المرور وتقييد المنفذ.

## 10. قواعد العمل التي لا تُكسر

- الشروح للمستخدم بالعربية ومن اليمين إلى اليسار. أسماء التقنية بالإنجليزية بين قوسين.
- أوامر PowerShell في كتلة من اليسار إلى اليمين وتبدأ بـ `Set-Location D:\Google\XtraPOS`.
- لا يُعطى المستخدم أمرا PowerShell معاً إذا كان الثاني يعتمد على نتيجة الأول. أمر واحد، ثم انتظار نتيجته.
- قواعد العمل `XtraPOS_Branch` و`XtraPOS_Central` فقط عند الحديث عن الإنتاج.
- التحديث يوقف الخدمة، يجلب الحزمة، ينسخها دون الإعدادات و`App_Data`، ثم يشغّل الخدمة.
- الجهاز الجديد يثبّت حزمة موقع الإعداد، وفيها الإعدادات.
- جرد الجرار: صفوف غير قابلة للتعديل، والقرار صف لاحق، والفئات فوق لوحة المفاتيح، وجملة إعادة العد مرة واحدة بين العدين.
- لا تُضاف مزامنة فرع بعيد وكأنها تعمل. العلم `BranchRemoteSync.IsImplemented` يساوي `false`.
- لا تُطبع أسرار، ولا تُحذف قواعد الإنتاج ضمن سكربت نشر.

---

## 11. أين تُقرأ التفاصيل التي لا يسعها هذا الملف

- أعمدة جدول: ملف `*.sql` الذي يطابق اسمه في القسم 5.1.
- سلوك نقطة البيع: `src/XtraPOS.Web/src/components/PosScreen.tsx` و`src/XtraPOS.Api/Sales/SalePostingService.cs`.
- سلوك الجرد: `CashDrawerScreen.tsx` و`CashDrawerReviewScreen.tsx` و`CashDrawerService.cs` و`CashDrawerRules.cs`.
- حلقة الترحيل: `CreditOutboxWorker.cs` و`SaleCentralRelay.cs` و`SharedDataSync.cs`.
- سجل جلسة أقدم، وقد تجاوزته الإصدارات المذكورة هنا: `XTRAPOS_SESSION_2026-11-20.md`.
