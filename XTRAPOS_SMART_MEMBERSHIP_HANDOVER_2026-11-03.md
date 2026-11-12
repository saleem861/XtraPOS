# XtraPOS — Smart Membership & Customer Verification

**ملف تسليم مرجعي لمحادثة متخصصة**  
**تاريخ الإعداد:** 03/11/2026  
**الحالة:** نُفذت أجزاء رئيسية في المستودع؛ لم تُفعّل العضوية على Staging ولم يُختبر تدفقها الحقيقي من البداية للنهاية.  
**النطاق:** نظام العضوية الذكية والتحقق عبر QR وتسجيل Google/Apple والجلسات والتكامل مع المبيعات والتسعير فقط.

> **تعليمات للمساعد في المحادثة الجديدة:** اعتبر هذا الملف نقطة استكمال العمل، لا طلبًا لإعادة التصميم أو البدء من الصفر. افصل هذا المسار عن Agent آخر يعالج «أنواع الزبائن / أكواد التسعير / اتصال المركز». استند إلى تقارير Cursor الفعلية، ولا تعتبر ما تم تنفيذه برمجيًا أو اجتياز اختبارات وحدات محدودة إثباتًا لعمله على Staging. المستخدم **أرسل بالفعل** إلى Agent العضوية تعليمات «Remaining Verification and Release Readiness» وهو بانتظار نتائجها. لا تطلب إعادة إرسالها، ولا تفترض تنفيذها أو نجاحها قبل وصول تقرير جديد.

---

## 1. القرارات المعتمدة من صاحب المشروع

1. الهدف الأساسي **سرعة التعرف إلى عضوية الزبون وتطبيق التسعيرة** عند نقطة البيع.
2. **لا يشترط التحقق من هوية الشخص البشري الذي يحمل الهاتف**. المطلوب التعرف على عضوية مسجلة ومصادق عليها. ينبغي عدم تقديم النتيجة على أنها إثبات هوية الشخص.
3. كان مطروحًا ربط العضوية بهاتف مادي بعينه، لكن **اعتمد لاحقًا أن تنتقل قابلية استعادة العضوية إلى هاتف جديد عبر حساب Google أو Apple المرتبط بالعضوية**. لذلك لا يُشترط جهاز مادي واحد ولا بصمة عتادية للجهاز. لا يلزم أن تنتقل جلسة XtraPOS نفسها تلقائيًا؛ قد يحتاج الزبون لتسجيل دخول لمرة واحدة على الهاتف الجديد.
4. في الزيارات العادية: **لا بصمة، لا OTP، ولا زر «تأكيد»** ما دامت جلسة XtraPOS صالحة. مسح QR وفتح رابطه يبدأ التحقق تلقائيًا.
5. عند **التسجيل/الربط لأول مرة** يجب إثبات حق ربط الحساب بزبون XtraPOS الموجود، مثل OTP على رقم الزبون المسجل؛ Google/Apple وحده أو تطابق البريد الإلكتروني لا يكفي للربط الأولي.
6. عند الكاشير: يحدد الكاشير زبونًا ثم يضغط **«التحقق من العضوية»**؛ يظهر QR على شاشة العميل، مع بديل ضمن POS حين لا تكون شاشة ثانية متاحة.
7. بعد التحقق الناجح، **تُفعّل التسعيرة الخاصة تلقائيًا** إذا كانت سياسة `RequireVerificationForSpecialPrices` مفعّلة، ويُعاد تسعير الأصناف الحالية دون زر إضافي ودون إفساد السعر اليدوي المصرح به.
8. الحفظ التلقائي المقصود: **حفظ ربط الهوية الخارجية وجلسة العضوية مباشرة بعد نجاح التسجيل الأولي**، وليس إلغاء زر «حفظ» الخاص ببيانات الزبون الأساسية. الزبون الجديد يجب حفظه أولًا للحصول على CustomerID صالح.
9. بعد البيع يعود الحساب إلى **Walk-in**، وتُلغى/تُصفّر حالة التحقق الخاصة بالفاتورة. المسودات المستعادة لا ترث Verified.
10. نظام الائتمان/الديون له سياسة وصلاحيات تحقق مستقلة؛ **نجاح عضوية QR ليس تفويضًا للبيع بالدين** أو لعمليات مالية حساسة.
11. البيع النقدي **Offline** مطلوب. تعطل المركز لا يجوز أن يعطل البيع النقدي الاعتيادي ولا أن ينتج تحقق عضوية وهميًا.
12. **Passkey ليس واجهة العضوية الأساسية**. الخدمات التشخيصية تبقى موجودة باسم **«تشخيص Passkey»**، دون حذفها من المشروع.
13. التطوير يتبع `XTRAPOS_UI_STANDARD v1.1`: العربية RTL، شاشة POS كاملة، Header Navy، لوحات الإدخال المشتركة، دعم Desktop وTablet ومنع تداخل لوحة المفاتيح.

### مسار التجربة المستهدف

```text
زبون موجود محفوظ في XtraPOS
     ↓ (أول مرة فقط)
تسجيل Google / Apple + إثبات حق ربط العضوية (مثل OTP)
     ↓
حفظ CustomerID ↔ Provider Subject وإنشاء جلسة XtraPOS آمنة
     ↓ (في زيارة لاحقة)
اختيار الزبون في POS → زر «التحقق من العضوية»
     ↓
QR مؤقت مرتبط بالزبون + الفرع + الكاونتر + سياق الفاتورة
     ↓
فتح الرابط على هاتف لديه جلسة عضوية صالحة
     ↓
POST تلقائي مصادق عليه ومحمّي ضد CSRF
     ↓
التحقق المركزي من الجلسة/الحساب/صلاحية QR/سياق البيع
     ↓
Verified → تحديث POS/شاشة العميل → إعادة تسعير السلة عند تفعيل السياسة
     ↓
إتمام البيع → Walk-in + إلغاء الطلبات المعلقة وحالة التحقق
```

**قيد مهم:** فتح رابط QR من نظام الهاتف قد يتطلب ضغطًا في واجهة الكاميرا أو المتصفح؛ المقصود عدم وجود **زر تأكيد إضافي داخل صفحة XtraPOS** عندما توجد جلسة صالحة.

---

## 2. معلومات المشروع والبيئات

| العنصر | المعلومة المعروفة |
|---|---|
| مستودع التطوير المحلي | `D:\Google\XtraPOS` |
| Frontend | React + TypeScript + Vite |
| Backend | ASP.NET Core / .NET 8 |
| Database | Microsoft SQL Server |
| Local API المعتاد | `http://localhost:5080` — **أُبلغ لاحقًا أنه متوقف**؛ لا تفترض أنه يعمل |
| Staging | `https://test-pos.sharbelfashion.com` |
| Staging service | `xtrapos-staging.service` |
| Staging local API | `http://127.0.0.1:5088` |
| Central Staging DB | `XtraPOS_Central_Test_Staging` |
| Branch Staging DB | `XtraPOS_Branch_Test_Staging` |
| Staging web root | `/opt/xtrapos-staging/current/web` |
| Staging backend | `/opt/xtrapos-staging/current/api` |
| Full staging deploy | `deploy/Deploy-StagingDirect.ps1` |
| Frontend-only deploy | `deploy/Deploy-StagingFrontendOnly.ps1` |
| آخر Release منشور أُبلغ عنه | `20261103-011511-aaba7df`، Live JS `/assets/index-Bpdgszxd.js` — يحتاج التحقق قبل أي نشر جديد |

**حالة مهمة:** في نشر Staging المذكور طُبقت Central migrations 019 و020 و021 وأصبح `/health` يُرجع Healthy؛ **وجود Migration 019 على Staging لا يعني تفعيل Smart Membership**. إصدار Staging والتكوين التشغيلي قد يتغيران لاحقًا بعد استلام تقارير جديدة.

### بيانات الربط ذات الصلة، لأغراض نطاق الأعمال لا لإصلاحه

- Business: **Smoker World / عالم المدخن**.
- BusinessID: `422cda3a-498b-4eae-9eda-1db23e399ae1`.
- Branch: **المقاطعة**، Branch code **03**.
- تصنيفات الزبائن السبعة وأكواد التسعير الثمانية تخص مسار Agent آخر؛ لا تعالجها في Agent العضوية.

---

## 3. الفصل الصارم عن Agent المرجعيات واتصال المركز

يوجد **Agent آخر نشط ومستقل** يعمل على:

- قوائم «نوع الزبون» و«نوع التسعيرة» و«نسبة الخصم».
- `GET /api/customers/classifications` و`GET /api/customers/price-types`.
- مشكلة ظهور «تعذر تحميل أنواع الزبائن» و«غير متصل بالإدارة المركزية».
- `SyncEndpointStore.cs` و`CustomerMasterService.cs` ومطابقة اتصال المركز، وترحيلات مرجعيات الزبائن.

**آخر تشخيص مستقل للـAgent الآخر:** كان تشغيل API الحي يعتمد على كلمة مرور `SyncEndpoints` لطلبات المرجعيات/الحالة، في حين أن Migrations نجحت باستخدام ملف اتصال المركز المحفوظ في `/opt/xtrapos-staging/shared/app-data`. نُفذ تعديل محلي لاستخدام الاتصال المحفوظ عند توفره، **ولم يُنشر وفق آخر تقرير خاص بذلك الإصلاح**. هذا السياق للمعرفة فقط.

**تعليمات إلزامية لAgent Smart Membership:**

- لا تتدخل في إصلاح قوائم أنواع الزبائن أو الترحيلات المرجعية أو إعداد الاتصال المركزي.
- لا تعدّل ملفات الـAgent الآخر أو تستبدل تعديلاته غير الملتزم بها.
- عند ضرورة تعديل ملف مشترك مثل `NewCustomerScreen` أو `Program.cs`، أنشئ مكوّنًا معزولًا قدر الإمكان ونسّق نقطة الدمج دون الكتابة فوق التغييرات المتزامنة.
- لا تستخدم `git reset`, `git clean` أو `stash` تلقائيًا ولا تتخلص من تغييرات غير محفوظة.
- اختبر العضوية بتجهيز بيانات اختبار مستقلة صحيحة، ولا تفسّر تعطل قوائم المرجعيات كفشل للعضوية نفسها.
- اختبارات التكامل الشامل مع بيانات الزبائن الحقيقية ستأتي عند اكتمال المسارين.

---

## 4. ما نفذه Cursor في Smart Membership وفق التقارير

### 4.1 قاعدة البيانات

Central migration:

- `019_SmartMembership.sql`: كائنات العضوية الأساسية.
- `020_MembershipAuthAttempts`: مسجل كتطبيق على Staging؛ تفاصيله الدقيقة تحتاج قراءة الملف إن لزم.

الكائنات الأساسية المبلغ عنها:

| الجدول | الغرض |
|---|---|
| `CustomerMembershipIdentities` | ربط CustomerID بهوية Google/Apple عبر Provider Subject |
| `CustomerMembershipSessions` | جلسات العضوية وإلغاؤها |
| `MembershipVerificationRequests` | طلبات QR المؤقتة وحالاتها |
| `MembershipVerificationAudit` | سجل أحداث العضوية والتحقق |
| `MembershipEnrollmentRequests` | عمليات تسجيل/ربط العضوية الأولية |

المذكور في التقرير:

- Provider Subject فريد لكل Business عندما يكون الربط فعّالًا.
- رموز الجلسات وQR تُخزّن على هيئة SHA-256 hashes، لا النصوص الأصلية.
- لا إنشاء تلقائي لزبائن إنتاج ولا تغيير الأرصدة/المخزون/دفاتر الائتمان.
- تم تطبيق 019 و020 على Staging في نشر سابق مع بقاء ميزات العضوية **معطلة**.

### 4.2 Backend / Authentication

- خدمات الربط والتحقق والإلغاء موجودة في مستودع المشروع.
- `MembershipAuthService` و`MembershipLinkPolicy` ذُكرا كمسؤولين عن إثبات الملكية الأولي، منع ربط مكرر، وإنشاء جلسة لمعرف Provider مرتبط من قبل.
- مسارات بدء Google/Apple والاستدعاءات المرتدة موجودة **لكنها BLOCKED_EXTERNAL_CONFIG** حتى ضبط قيم مزود الهوية.
- عند أول ربط مطلوب إثبات ملكية حساب الزبون مثل OTP؛ عند تسجيل الدخول اللاحق بنفس `Provider Subject` المرتبط لا يُطلب OTP ربط جديد.
- قيل إن رابط Provider/جلسة العضوية يُحفظ ضمن معاملة الربط، **لكن لم يتم تأكيد ذلك على مزود حقيقي**.
- Endpoint لإلغاء كل جلسات زبون: `POST /api/customers/{id}/membership/revoke-all`، بصلاحية إدارية مناسبة.
- إلغاء الربط إجراء منفصل للموظف المخول.

### 4.3 QR / الحماية

- رمز QR عشوائي، يُحفظ hash، صلاحيته الافتراضية **60 ثانية** من `QRExpirySeconds`.
- `GET` لا ينفذ عملية الاعتماد.
- طلب `POST` من الهاتف يشترط Cookie جلسة عضوية `HttpOnly` وCSRF header مطابقًا، وتتحقق الخدمة من الطلب.
- إذا أعاد الخادم `MEMBERSHIP_SESSION_REQUIRED` تظهر خيارات تسجيل Google/Apple بدل نجاح وهمي.
- تحديث حالة `Pending → Verified` مشروط ومفترض أن يكون أحاديًا داخل قاعدة البيانات؛ **التزامن الحقيقي لم يختبر بعد عبر SQL**.
- آخر تطوير: صفحة الهاتف **لم تعد تحاول قراءة HttpOnly Cookie من JavaScript**؛ ترسل الطلب تلقائيًا ويحكم Backend بالجلسة.
- آخر تطوير: `POST /api/pos/membership-verifications/cancel-sale` لإلغاء الطلبات المعلقة للكاونتر وسياق الفاتورة عند تغيير الزبون أو العودة إلى Walk-in أو استعادة مسودة أو إتمام البيع.

### 4.4 الشاشات والأزرار

| الشاشة | السلوك المبلّغ عنه |
|---|---|
| `NewCustomerScreen` | قسم «العضوية الذكية»، تسجيل Google، تسجيل Apple، إدارة الجلسات، إلغاء الربط بإذن إداري، وتشخيص Passkey |
| `PosScreen` | **«التحقق من العضوية»** بدل زر Passkey الأساسي؛ QR يبدأ من هذا التحكم، وليس تلقائيًا بمجرد اختيار الزبون |
| `/membership/verify/{token}` | صفحة الهاتف؛ إرسال التحقق آليًا عند وجود جلسة، دون زر موافقة |
| `/membership/ready` | تعرض أن عملية التسجيل محفوظة |
| `/membership/display?counter={stationId}` | شاشة العميل الثانية؛ تعرض QR للكاونتر/الفاتورة، ثم «تم التحقق من العضوية»، وتزيله إذا لم يعد Pending |
| POS fallback | QR نفسه يظهر في POS إن لم تستجب شاشة العميل الثانية |
| Passkey | بقيت خدمات/واجهات تشخيصية؛ لم تُحذف APIs |

**تنبيه:** نجاح الشاشة الثانية في منع تداخل الكاونترات ورد في الكود والتقرير، **لم يُثبت باختبار تشغيل فعلي متعدد الكاونترات**.

### 4.5 التسعير ودورة البيع

- ملف `membership/membershipPricing.ts` يفصل أهلية استخدام CustomerID لأغراض التسعير عند تفعيل سياسة التحقق.
- عند `Membership:RequireVerificationForSpecialPrices=true`:
  - قبل Verified: لا تُفعّل تسعيرة العضوية المقيدة بالتحقق.
  - بعد Verified: يُعاد تمرير CustomerID إلى مسار `repriceCart` الحالي لتسعير السلة، ويُفترض الحفاظ على التعديلات اليدوية المصرح بها والكميات.
- عند `RequireVerificationForSpecialPrices=false`، اختيار الزبون **يطبق سعره مباشرة كما كان قبل نظام العضوية**.
- تغيير الزبون، Walk-in، استعادة مسودة، أو إتمام البيع: تفريغ Verified واستدعاء `cancel-sale` على الخادم.
- استعادة المسودة لا تورث Verified؛ ووفق تقرير Cursor لا تحتفظ المسودة المستعادة بأسعار عضوية مقيدة عند تفعيل سياسة التحقق.
- مسار الائتمان/الديون مستقل؛ لا يسمح نجاح عضوية QR بتجاوز حدود الائتمان أو التفويض المالي.

### 4.6 إعدادات Source Defaults (لم تتغير)

```ini
Membership:Enabled=false
Membership:GoogleLoginEnabled=false
Membership:AppleLoginEnabled=false
Membership:RequireVerificationForSpecialPrices=false
```

**لا تخلط بين موجود في Source وبين مفعل على Staging**. آخر تقرير يؤكد أنه **لم يجرِ نشر تطويرات Smart Membership الأخيرة، ولم تُفعّل الأعلام**.

---

## 5. ملفات التنفيذ المعروفة

### Backend

```text
src/XtraPOS.Api/Data/Central/Migrations/019_SmartMembership.sql
src/XtraPOS.Api/Membership/MembershipRules.cs
src/XtraPOS.Api/Membership/MembershipTokens.cs
src/XtraPOS.Api/Membership/MembershipService.cs
src/XtraPOS.Api/Endpoints/MembershipEndpoints.cs
```

بالإضافة إلى الأسماء المذكورة في التقرير الأحدث:

```text
MembershipAuthService
MembershipLinkPolicy
```

افحص المسارات الفعلية لهذه الأخيرة ولا تفترض مكانها.

### Frontend

```text
src/XtraPOS.Web/src/components/MembershipQr.tsx
src/XtraPOS.Web/src/components/MembershipPhonePage.tsx
src/XtraPOS.Web/src/components/MembershipPhonePage.test.tsx
src/XtraPOS.Web/src/components/MembershipDisplayPage.tsx
src/XtraPOS.Web/src/membership/membershipPricing.ts
src/XtraPOS.Web/src/membership/membershipPricing.test.ts
src/XtraPOS.Web/src/components/PosScreen.tsx  # الاسم الظاهر في التقرير؛ تأكد من المسار الفعلي
src/XtraPOS.Web/src/ApplicationRoot.tsx       # الاسم الظاهر في التقرير؛ تأكد من المسار الفعلي
```

> ملاحظة: ذُكرت `NewCustomerScreen`, `Program.cs`, `appsettings.json` كملفات ذات علاقة، وقد يعدلها Agent آخر؛ افحص الملكية قبل تعديلها.

### Tests / Docs

```text
tests/XtraPOS.Api.Tests/MembershipRulesTests.cs
docs/XTRAPOS_SMART_MEMBERSHIP.md
```

### API routes المبلغ عنها

```http
POST /api/pos/membership-verifications/cancel-sale
POST /api/customers/{id}/membership/revoke-all
GET  /membership/verify/{token}                 # صفحة ويب
GET  /membership/ready                          # صفحة ويب
GET  /membership/display?counter={stationId}    # صفحة ويب
```

يوجد أيضًا مسارات إنشاء/قراءة/إلغاء طلبات QR ومسارات Google/Apple start/callback؛ ينبغي **استخراج المسارات الدقيقة من `MembershipEndpoints.cs`** قبل إعداد اختبار أو وثيقة API. لا تفترض أن جميع المسارات المقترحة سابقًا استُخدمت حرفيًا.

### Redirects المقترحة على Staging

```text
https://test-pos.sharbelfashion.com/api/membership/auth/google/callback
https://test-pos.sharbelfashion.com/api/membership/auth/apple/callback
```

هذه القيم وردت من Cursor كمتطلبات إعداد، ويجب مطابقتها مع المسارات الفعلية المسجلة قبل تسجيلها لدى Google/Apple.

---

## 6. الاختبارات: ما ثبت وما لم يثبت

### نتائج مُبلغ عنها في مراحل مختلفة

| المرحلة | نتيجة التقرير | ماذا تثبت؟ |
|---|---|---|
| تنفيذ أولي | `MembershipRulesTests`: 13 PASS | قواعد مختارة فقط |
| مرحلة تالية | `MembershipRulesTests`: 15 PASS | قواعد مختارة في نسخة سابقة |
| آخر تنفيذ محدد | `MembershipPhonePage.test.tsx` + `membershipPricing.test.ts`: **4 PASS** | صفحة الهاتف ومنطق بوابة التسعير على مستوى وحدات |
| آخر تنفيذ محدد | `dotnet build src/XtraPOS.Api/XtraPOS.Api.csproj`: PASS | بناء Backend |
| آخر تنفيذ محدد | `npm run build`: PASS | بناء Frontend |
| آخر تنفيذ محدد | `MembershipRulesTests`: **لم تُشغّل** | لا توجد نتيجة جديدة في ذلك التقرير |

**عائق آخر تشغيل للاختبارات الخلفية:** مشروع اختبارات API لم يكن يُجمّع لأن `CreditSaleIntegrationTests` يستخدم منشئ `SyncEndpointStore` قديمًا بعد تغييرات Agent الآخر. Agent العضوية **لم يعدّل الملف** حتى لا يتداخل مع المسار الموازي. ينبغي معالجة التوافق بأقل نطاق وبالتنسيق، لا تعطيل اختبارات الائتمان أو استثناءها للتظاهر بالنجاح.

### الاختبارات التي تبقى غير مثبتة (أولوية)

- SQL integration على قاعدة Central اختبارية معزولة.
- حالتا QR متزامنتان وتأكيد ذري لا ينتج موافقتين.
- صلاحية الجلسة بعد Restart، والإلغاء الفوري للجلسات.
- CSRF، Replay، Wrong Customer، Wrong Business، Wrong Counter، Expiry باختبارات تكامل فعلية.
- إلغاء طلب QR من الخادم عند تغيير العميل وإتمام البيع/استعادة المسودة.
- تدفق POS الحقيقي من التحقق إلى التسعير، وإعادة تسعير السلة مع اليدوي والكميات.
- عدم تسرّب QR بين شاشتَي كاونترين.
- اختبارات Non-regression للمبيعات النقدية والائتمان والمسودات وOffline.
- تسجيل Google وApple الحقيقي، وربط عضوية لأول مرة ثم استعادتها على هاتف آخر.
- مسح QR على Android Chrome وiPhone Safari.
- التجربة الكاملة على Staging قبل اعتماد النظام.

**معيار التقارير:** PASS / FAIL / BLOCKED / PENDING_MANUAL / BLOCKED_EXTERNAL_CONFIG، ولا PASS دون نتيجة اختبار فعلية.

---

## 7. العوائق الحالية

### 7.1 Google / Apple (BLOCKED_EXTERNAL_CONFIG)

لا يوجد إعداد تشغيلي مؤكد لكل من:

**Google:**

- OAuth/OpenID Connect Client ID.
- Client Secret عند الحاجة لتدفق الويب الخلفي.
- Authorized redirect URI على نطاق Staging.
- إعدادات consent/authorized domains حسب حالة التطبيق.

**Apple:**

- Team ID.
- Services ID.
- Key ID.
- Private signing key.
- Domain verification.
- Return URL.

**التهيئة الأولية:** مزود OTP فعلي صالح لربط الحساب لأول مرة، أو مسار ملكية حساب معتمد مكافئ. لا يجوز تجاوز هذا الإجراء فقط لتنجح التجربة.

**قواعد أمان:** لا تلصق أسرار في المحادثة أو التقارير أو Git؛ استخدم مخزن الإعدادات/البيئة المعتمد، وقيّد أسرار Staging ضمن Staging.

### 7.2 الاختبارات والتزامن

- مشكلة تجميع مشروع الاختبارات المتأثر بمنشئ `SyncEndpointStore` لدى Agent آخر.
- اختبارات SQL وQR concurrency وREST/Restart وPOS browser لم تُثبت بعد.
- لا توجد تجربة هواتف فعلية أو تسجيل مزود هوية فعلي.

### 7.3 النشر والتفعيل

- آخر التعديلات على Smart Membership **لم تُنشر**.
- Source defaults كلها `false`.
- Staging لديه Central Migration 019 و020 وفق سجل نشر سابق، لكن ذلك لا يثبت عمل خدمة العضوية الحية.
- لا تنشر شجرة Git المتسخة بصورة شاملة دون تدقيق، خصوصًا مع Agent آخر يعدّل Backend واتصال المركز.

---

## 8. آخر تعليمات أُرسلت بالفعل إلى Agent العضوية

أبلغ المستخدم صراحة أنه **أرسل التعليمات** التالية (ملخصها التنفيذي، وليس تكليفًا جديدًا):

**عنوان المهمة:** `XtraPOS Smart Membership — Remaining Verification and Release Readiness`

1. فحص تعارض تجميع اختبارات API الناشئ عن `CreditSaleIntegrationTests` مقابل `SyncEndpointStore`، مع حل التوافق دون تخريب عمل Agent الآخر ودون تعطيل الاختبارات.
2. تنفيذ اختبارات تكامل SQL فعلية للجلسات والإلغاء وتزامن Pending→Verified وWrong Customer/Business/Counter وReplay/Expiry وإلغاء `cancel-sale`.
3. اختبار تسعير POS بميزات تفعيل الاختبار:
   ```ini
   Membership.Enabled=true
   Membership.RequireVerificationForSpecialPrices=true
   ```
   إثبات السعر قبل التحقق وبعده، إعادة تسعير السلة، حفظ التعديلات اليدوية، Walk-in، المسودات، والمبيعات النقدية/الائتمان.
4. اختبار `/membership/display` متعدد الكاونترات وعدم كشف معلومات لعملاء غير مصرح لهم.
5. تحضير قائمة إعداد Google وApple الفعلية: أسماء المتغيرات، النطاق، الـredirects، التخزين الآمن، التسجيل الأول والجهاز الجديد.
6. تقرير جاهزية النشر والأعطال المتبقية، مع **عدم النشر في هذه المهمة**.

**العمل التالي في المحادثة الجديدة:** اطلب من المستخدم **تقرير Cursor الناتج عن هذه المهمة** عند توفره، ثم راجعه وقرّر أقل خطوة تالية مطلوبة. لا تعِد إرسال الأمر نفسه أو تبدأ تصحيح القوائم التي تخص Agent الآخر.

---

## 9. حالات قبول نهائية لنظام Smart Membership

| ID | سيناريو القبول | المطلوب |
|---|---|---|
| M01 | أول ربط Google/Apple | إثبات ربط العضوية ثم حفظ Provider Subject وSession تلقائيًا |
| M02 | دخول بحساب مرتبط | Session جديدة بلا OTP ربط أولي مكرر |
| M03 | هاتف جديد بحساب Google/Apple نفسه | استعادة CustomerID الموجود دون زبون مكرر |
| M04 | QR لزبون مطابق وله جلسة | POST تلقائي → Verified دون زر تأكيد |
| M05 | لا جلسة | LOGIN_REQUIRED؛ لا نجاح وهمي |
| M06 | Wrong Customer | رفض وعدم تفعيل التسعيرة |
| M07 | Expired/Replay/Cancelled QR | رفض وعدم توريث Verified |
| M08 | كاونتر آخر | لا تسرب ولا اعتماد لفاتورة أخرى |
| M09 | 2 طلبات متزامنة | استهلاك ذري واحد ولا نجاح مكرر |
| M10 | تسعير مشروط قبل Verified | التسعيرة الخاصة المقيدة لا تُطبّق |
| M11 | التسعير فور Verified | تطبيق Customer Price Type وإعادة تسعير السلة دون زر إضافي |
| M12 | Manual Override / Quantity | احترام الأسبقية القائمة وعدم تكرار الخصومات |
| M13 | تغيير الزبون/إلغاء البيع/مسودة | إلغاء QR وClear Verified على الخادم والواجهة |
| M14 | إنهاء البيع | العودة إلى Walk-in ومنع إعادة استعمال التحقق |
| M15 | Session revoked/expired | الرفض الفوري والتعامل مع تسجيل الدخول |
| M16 | Offline | يستمر البيع النقدي دون نجاح عضوية غير مؤكد |
| M17 | شاشة عميل ثانية | QR للكاونتر الصحيح، الحالة تتحدث، fallback POS |
| M18 | صلاحيات إدارة الجلسات/فك الربط | لا يسمح للموظف غير المخول |
| M19 | استمرارية جلسة بعد Restart | جلسة سارية تظل صالحة إذا لم تُلغَ أو تنتهِ |
| M20 | Staging E2E فعلي | Enrollment → QR → Price → Sale → Walk-in على هاتف فعلي |

قبول كل حالة يعتمد على الدليل المباشر (اختبار حقيقي أو Test result فعلي)، مع التفريق بين المصدر المحلي والبيئة المنشورة.

---

## 10. خطة العمل المتبقية بعد وصول التقرير الحالي

1. **قراءة تقرير Agent العضوية الجديد** وتحديد ما إذا حُل تعارض الاختبارات ونُفّذت اختبارات SQL وPOS وشاشة العميل.
2. عند وجود فشل: إصلاح محدد في نطاق Smart Membership فقط، دون مسار مرجعيات الزبائن.
3. **إعداد Google أولًا** على Staging لتسريع تجربة Android، ثم Apple مع بيانات Developer المطلوبة. لا يشترط انتظار Apple لتجربة Google إذا كانت الهوية/OTP/الأمان صحيحة وكانت الميزة معزولة خلف flags.
4. تجهيز اختبار شامل محلي/معزول للأعلام `Enabled` و`RequireVerificationForSpecialPrices` ثم Staging بعد الموافقة الصريحة.
5. عدم نشر نسخة Backend كاملة من Dirty Tree لم تُراجع خاصة مع عمل Agent الموازي؛ توثيق SHA وهوية الحزمة ومحتواها.
6. تنفيذ اختبار Android/iPhone حقيقي، وتتبع نجاح التسجيل والربط والجلسة وQR وإعادة التسعير وإعادة Walk-in.
7. قبول النظام فقط عند نجاح Gates ذات الصلة، مع عدم اختلاق نتائج Google/Apple أو المتصفح.

---

## 11. احتياطات هندسية وتشغيلية دائمة

- **Production ممنوع:** لا تغيير DB الإنتاج، لا restart لـ`xtrapos-api.service`، لا تبديل مسارات الإنتاج ولا استيراد زبائن إنتاج.
- **Resource-Saving Mode:** نطاق Smart Membership فقط، لا refactoring شامل، اختبارات مركزة، إعادة استخدام الوظائف الموجودة.
- **لا إفشاء أسرار**: كلمات مرور SQL، Client Secrets، Apple private keys، Cookies، CSRF Tokens، QR raw tokens، OTP، Provider tokens.
- **لا نجاح بدون دليل:** البناء والاختبارات وحدها لا تكفي لإعلان تفعيل Google/Apple أو عمل QR على هاتف حقيقي.
- **ربط ملكية الحساب أول مرة** يجب ألا يُلغى حتى وإن كان المطلوب عدم التحقق من هوية الشخص في كل شراء.
- **No GET side effects:** صفحة QR قد تُفتح من ماسح روابط أو Prefetch؛ اعتماد العضوية يجب أن يكون POST مصادقًا عليه ومحميًا من CSRF.
- **عدم تجاوز السياسات المالية:** QR يثبت جلسة العضوية فقط؛ لا ينشئ طلب دين أو تفويض دفع ولا يغير حد الائتمان.
- **مزامنة الحالة مع البيع:** `cancel-sale` وسياق الفاتورة/الكاونتر وWalk-in لتجنب تسعير فاتورة جديدة بعضوية سابقة.
- **الحفظ والتسعير مفهومـان منفصلان:** ربط Google/Apple يُحفظ بعد Enrollment الصحيح، وتسعيرة العضوية تُطبّق بعد Verified إذا كان العلم مفعّلًا.
- **المتصفح لا يقرأ HttpOnly cookie**: يجب الاعتماد على إرسال Cookie تلقائيًا في الطلب ثم فحصها بالخادم.
- **التحقق من إعدادات المزود** دون إضعاف التوقيع، Issuer، Audience، State، Nonce وPKCE وفق طريقة التدفق.
- **Staging فقط بعد الموافقة:** لا نشر تلقائي من تقرير Cursor، ولا تفعيل مزود مفقود الإعدادات؛ استخدم flags بيئية.

---

## 12. افتتاحية مقترحة للمحادثة الجديدة

> هذا ملف تسليم متخصص بـ XtraPOS Smart Membership. اقرأه بالكامل واعتمد تفاصيله كمرجع استمرار. يوجد Agent مستقل يعمل على أنواع الزبائن واتصال المركز؛ لا تخلط المسارين. أرسلت بالفعل إلى Agent العضوية تعليمات الاختبارات والتجهيز النهائي، وسأرسل لك تقريره التالي. أريد مراجعة النتائج ثم إعطائي الخطوة التالية فقط، دون إعادة شرح المشروع أو إعادة تكليف بما تم إرساله، ومع الحفاظ على أمان Production وعدم اعتماد اختبار لم ينفذ.

---

**نهاية ملف التسليم — XtraPOS Smart Membership — 03/11/2026**
