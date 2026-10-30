# XtraPOS — Handover

## Checkpoint status (26 Oct 2026, not synced)

- Branch: `master`
- Local HEAD: `53e60c46c4684e75daf9774cbffe40162925ccc4`
- Remote: none. No upstream. Nothing is waiting on a remote because no remote exists.
- Push: not done. Do not treat this PC and the Gold Market PC as synchronized.
- This status section itself is an uncommitted edit made after that commit.

Gold Market identity to reuse, not recreate: branch code `02`, BranchID `5C0FA56C-79C8-F111-B934-D43B046FDF64`, station كاونتر 1, device صندوق 1. Read the effective database name from that machine’s user secrets at runtime. A name ending in `_01` or `_02` does not identify the business branch.

Migrations present in source, not applied by this handover: branch `019`–`023`, central `006`–`010`. They run only when that PC starts the API from the project directory or the existing migration commands are run there. Do not copy databases.

Run from this repo: API `dotnet run --launch-profile http` in `src/XtraPOS.Api` (port 5080, one instance). Web `npm run dev` in `src/XtraPOS.Web` (port 5173).

Verified in source on this commit: sales screen, held drafts, customer search, exceptional prices, shared full keyboard, shared numeric keypad, A4 preview for the open cart, customer reset after successful checkout. WhatsApp and QR stay disabled until a public HTTPS invoice host exists. That share path was not verified with an external phone.

تاريخ التسليم: 26 أكتوبر 2026  
المسار: `C:\Users\Saleem Jayousi\OneDrive\Projects\XtraPOS`  
المالك: Salim (Software engineer)

هذا الملف يلخّص ما بُني في هذه الجلسة حتى تُكمل العمل في محادثة جديدة دون إعادة اكتشاف القرارات.

---

## 1. ما هو التطبيق

نظام نقاط بيع عربي (RTL) من جزئين:

- API: ASP.NET Core `net8` في `src/XtraPOS.Api`
- واجهة: React + Vite في `src/XtraPOS.Web`

قواعد البيانات (SQL Server، Windows auth عبر أسرار التطبيق وليس `appsettings` فقط):

| القاعدة | الاسم الفعلي |
|---|---|
| الفرع | `XtraPOS_Branch_01` |
| المركزي | `XtraPOS_Central` |

لا تستخدم الاسم `XtraPOS_Branch` من `appsettings` كاسم فرع مؤكد. اسم الفرع المستخدم في الأسرار هو `XtraPOS_Branch_01`.

التشغيل المحلي:

- API: من `src/XtraPOS.Api` بـ `dotnet run --launch-profile http` على `http://0.0.0.0:5080`
- الواجهة: `http://localhost:5173`
- لا تشغّل نسخة API ثانية. القفل يكون على `XtraPOS.Api.exe`
- تشغيل الـ exe من مجلد `bin` يفشل فك حماية البيانات. نفّذ من مجلد المشروع:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
dotnet exec .\bin\Debug\net8.0\XtraPOS.Api.dll --apply-central-migrations --confirm-database XtraPOS_Central
dotnet exec .\bin\Debug\net8.0\XtraPOS.Api.dll --apply-branch-migrations --confirm-database XtraPOS_Branch_01
```

أسرار الاتصال في .NET User Secrets: `SqlServer:Server=localhost` و `SqlServer:Database=XtraPOS_Branch_01`. لا تطبع رموز Meta أو Twilio.

آخر حالة تشغيل معروفة: API أُعيد تشغيله مرة واحدة بناءً على طلب صريح. الصحة كانت `{"status":"Healthy"}`. لا تفترض أن العملية ما زالت شغّالة.

---

## 2. ما اكتمل وما توقف

### متوقف: WhatsApp Cloud API (Meta)

المطلوب كان إرسال OTP حقيقي عبر Meta لرقم المرسل `+12027739269` مع الإبقاء على `PhoneVerification:Provider = TwilioVerify` إلى أن يُرسل OTP واتساب ويُستلم ويُتحقق منه.

- لا تبدّل المزود قبل إثبات واتساب حي.
- لا fallback من واتساب إلى SMS.
- لا تعيد المحاولة بـ `+972` / `+970` لأخطاء التوكن أو القالب أو WABA أو الإعداد.
- القالب: `xtrapos_customer_verification`، نوع AUTHENTICATION، عربي، OTP من 4 أرقام.
- `PhoneNumberId` هو معرّف Meta وليس رقم الهاتف `12027739269`.
- الاختبارات لا تتصل بـ Meta الحي.
- التوقف كان على شاشة القبول القانوني في Meta أثناء إضافة الرقم إلى التطبيق. المستخدم لم يكملها. لا تُكمل هذا المسار إلا إذا أكّد Salim أن الحوار القانوني أُغلق.

### مكتمل في الكود: تسعير الكمية الاستثنائي

قاعدة واحدة لكل زبون+صنف.

- `MinimumQuantity` + `BundlePrice`
- `SpecialPrice` يبقى سعر الوحدة المستهدف = `BundlePrice / MinimumQuantity` (4 منازل، AwayFromZero)
- إذا الكمية ≥ الحد، سعر الوحدة المستهدف يطبَّق على السطر كله (ليس تسعير كتل)
- تحت الحد: الرجوع إلى `PriceType` العادي للزبون
- الصفوف القديمة: `MinimumQuantity = 1` و `BundlePrice = SpecialPrice` القديم
- التحقق: الكمية > 0، سعر المجموعة > 0، السعر المستهدف > 0
- المركزي هو المرجع. الفرع نسخة محلية.
- ترتيب السعر: تجاوز يدوي للسطر، ثم سعر استثنائي مؤهل، ثم PriceType، ثم Price0
- تغيير الكمية أو الزبون يعيد تسعير الأسطر غير اليدوية
- السعر الأصلي لا يتغير
- لقطة سطر البيع عند تطبيق السعر فعلياً: `AppliedSpecialPriceID`, `AppliedMinimumQuantity`, `AppliedBundlePrice`, `AppliedTargetUnitPrice`, `ResolvedPriceSource = CustomerItemSpecial`
- نقطة البيع تستخدم بيانات الفرع. الخادم يعيد الحل عند الدفع.
- مسودات الزبون الجديد تبقى في الواجهة حتى الحفظ. الزبون الحالي يحمّل `CustomerItemSpecialPrices`.
- لم يُنفَّذ سيناريو البيع الحي (كمية 9 ثم 10 ثم 15 ثم 9) لأن المستخدم أوقف التحقق الحي حتى تكتمل الواجهة. الواجهة اكتملت بعدها ولم يُطلب إعادة التحقق الحي.

مثال الحساب: سعر عادي 5.50، كل 10 بـ 45.00 يعني سعر وحدة 4.50. الكمية 9 تبقى 5.50. من 10 فما فوق 4.50 لكل الوحدة.

### مكتمل: شاشات لوحة المفاتيح والبحث

انظر القسم 4. هذه أصبحت معايير ثابتة للمشروع.

---

## 3. قواعد البيانات ذات الصلة بهذه الجلسة

الهجرات مزدوجة: فرع في `src/XtraPOS.Api/Data/Migrations` ومركزي في `src/XtraPOS.Api/Data/Central/Migrations`.

| الفرع | المركزي | الموضوع |
|---|---|---|
| `019_ArabicSearchKeys.sql` | `006_ArabicSearchKeys.sql` | مفاتيح بحث عربي |
| `020_CustomerVerificationAndSpecialPrices.sql` | `007_CustomerVerificationAndSpecialPrices.sql` | تحقق الهاتف وأسعار استثنائية |
| `021_TwilioPhoneVerification.sql` | `008_TwilioPhoneVerification.sql` | Twilio Verify |
| `022_RegistrationPhoneVerification.sql` | `009_RegistrationPhoneVerification.sql` | تحقق هاتف التسجيل |
| `023_QuantitySpecialPrices.sql` | `010_QuantitySpecialPrices.sql` | كمية + سعر مجموعة + لقطة سطر البيع |

`023` / `010` طُبّقتا. المركزي وصل إلى 10 هجرات. الفرع كان 022 مطبقاً ثم طُبقت 023 (العدد 23). جدول الأسعار الاستثنائية على الفرع كان فارغاً فلم توجد صفوف backfill للفحص.

أعمدة اللقطة على `SaleItems` قابلة للإفراغ: `AppliedSpecialPriceID`, `AppliedMinimumQuantity`, `AppliedBundlePrice`, `AppliedTargetUnitPrice`.

`CustomerItemSpecialPrices`: `MinimumQuantity decimal(18,4)` افتراضي 1، `BundlePrice decimal(19,4)`. `SpecialPrice` يبقى سعر الوحدة المستهدف.

---

## 4. معيارا الواجهة الدائمان

### أ) لوحة XtraPOS الكاملة (عربي + أرقام)

أي شاشة Desktop/Laptop تعرض اللوحة الكاملة:

- ملء الشاشة داخل XtraPOS، ليست Modal ولا Popup ولا Dialog
- اللوحة = 33% من ارتفاع حاوية الشاشة المفتوحة (`.new-customer`)
- المحتوى = 67% المتبقية، `min-height: 0`، والتمرير داخلي فقط
- ممنوع `clamp()` وارتفاع ثابت بالبكسل يغيّر النسبة
- المكوّن الوحيد: `src/XtraPOS.Web/src/components/FullKeyboard.tsx`
- عربي: 24px / 700 / line-height 1
- أرقام ومشغّلات و`00`: 22px / 700 / line-height 1
- العرض: عربي 19fr وأرقام 6fr (حوالي 76/24)، فجوة 8px
- Tablet/iPad: لا تُرسم اللوحة. لوحة النظام الأصلية

الشاشات التي تستخدمها الآن:

- زبون جديد `NewCustomerScreen.tsx`
- الأسعار الاستثنائية `ExceptionalPricesScreen.tsx` (صفحة داخل نفس الغلاف، ليست نافذة)
- بحث الزبون `بحث عن زبون` في `PosScreen.tsx`

قياسات مؤكدة لبحث الزبون: 1366×768 → لوحة 253 من 768 (33%). 1920×1080 → 356 من 1080 (33%).

بحث الأصناف `بحث عن صنف` ما زال على اللوحة القديمة داخل `pos-overlay`. لا تغيّره إلا بطلب صريح.

### ب) لوحة الأرقام فقط

أي إدخال رقمي بحت على Desktop/Laptop يستخدم:

`src/XtraPOS.Web/src/components/NumericKeypad.tsx`

المصدر البصري هو شاشة كمية نقطة البيع:

- المفاتيح: `1 2 3 / 4 5 6 / 7 8 9 / مسح 0` ومع `.` إذا سُمح بالكسر
- `مسح` يفرّغ الحقل النشط
- Backspace الفيزيائي يحذف خانة
- النوافذ الرقمية تبقى Modal. لا تُحوَّل إلى ملء الشاشة
- الخط على سطح المكتب يطابق كمية نقطة البيع: inherit، 36px، وزن 700، line-height 1 (وليس 22px؛ 22px خاص بلوحة الأحرف الكاملة)
- المكوّن لا يعرف قواعد التسعير. الشاشة تتحقق وتحسب

استُبدل به:

- `NumericEditModal` (كمية، سعر وحدة، إجمالي)
- لوحة الدفع النقدي/المختلط
- الرصيد الافتتاحي والمبلغ المعدود على سطح المكتب فقط
- نافذة `كل X بـ Y` في الأسعار الاستثنائية

لم تُستبدل:

- لوحة الشريط الجانبي في نقطة البيع (فيها مشغّلات وDel/Enter للإدخال السريع)
- لوحة بحث الأصناف
- حقل نسبة الخصم داخل نموذج الزبون (حقل مع اللوحة الكاملة، وليس نافذة أرقام)
- لا توجد شاشات جرد/مرتجعات/خصم منفصلة بلوحة أرقام لتُحوَّل

Tablet: نوافذ الكمية والسعر تخفي اللوحة المخصصة وتستخدم لوحة النظام. الدفع على التابلت ما زال يعرض لوحة على الشاشة لأنه كان كذلك أصلاً.

### بحث الزبون — الأعمدة

خمسة أعمدة في صف واحد، شبكة مشتركة للرأس والصفوف:

`16% رقم الزبون / 31% اسم الزبون / 19% الهاتف / 17% نوع الزبون / 17% التسعيرة`

السبب السابق للالتفاف: النتائج كانت ترث شبكة بحث الأصناف ذات 3 أعمدة. التمرير عمودي داخل النتائج. لا تمرير أفقي للصفحة.

---

## 5. أنواع الزبائن (بدون GUID في الواجهة)

| الاسم | PriceType | الخصم |
|---|---|---|
| عادي | Price0 | 0% |
| برونزي | Price3 | 12% |
| فضي | Price2 | 18% |
| ذهبي | Price1 | 22% |
| محل تجاري | Dealer أو DealerPrice | 31.81818% |
| زبون مميز | SpecialCustomer أو SpecialCustomerPrice | 10% |
| مستهلك كبير | MajorCustomer أو MajorCustomerPrice | 26.2626% |

ترتيب إدخال زبون جديد: الرقم، الاسم، الهاتف، الدولة، النوع، الملاحظات، إرسال الرمز، OTP من 4 أرقام، تحقق، حفظ. نوع السعر والخصم لا يُطلبان كخطوة مستقلة. الملاحظات في أقصى يسار الصف الثاني. الأسعار الاستثنائية زر واحد + سطر ملخص.

---

## 6. ملفات الواجهة الأساسية التي تغيّرت

- `src/XtraPOS.Web/src/components/FullKeyboard.tsx` — اللوحة الكاملة المشتركة
- `src/XtraPOS.Web/src/components/NumericKeypad.tsx` — لوحة الأرقام المشتركة
- `src/XtraPOS.Web/src/components/NumericEditModal.tsx` — تستخدم NumericKeypad على المؤشر الدقيق فقط
- `src/XtraPOS.Web/src/components/NewCustomerScreen.tsx`
- `src/XtraPOS.Web/src/components/ExceptionalPricesScreen.tsx`
- `src/XtraPOS.Web/src/components/PosScreen.tsx`
- `src/XtraPOS.Web/src/styles/app.css`
- `src/XtraPOS.Web/src/api/pricing.ts` — `resolveFixed` يقبل quantity و `applySpecial`
- `src/XtraPOS.Web/src/cart/model.ts` — إعادة التسعير تمرّر كمية السطر

الخلفية ذات الصلة (لا تُعاد كتابتها إلا لخلل عقد):

- `PricingResolver.cs` — `ResolveFixedPriceAsync(..., quantity, applySpecial)`
- `CustomerItemSpecialPriceService.cs`
- `CheckoutService.cs` و `SalePostingService.cs` يمرران كمية السطر ويخزّنان اللقطة
- `PricingEndpoints.cs`
- الهجرات 023 و 010

اختبارات مركّزة سبق أن نجحت: `WhatsAppAndSpecialPriceTests` + `PricingResolverTests` (11). بعد لوحة الأرقام: `NumericEditModal.test.tsx` و `ExceptionalPricesScreen.test.tsx` نجحا، واختبار تعديل الكمية في `PosScreen.test.tsx` نجح.

فشل واحد غير متعلق بلوحة الأرقام:  
`PosScreen.test.tsx` → `keeps suggestions out of fast entry and preserves the draft`  
توقع استدعاءَين لـ `variable-value` وظهر 4. لم يُصلح في هذه الجلسة.

لا تشغّل مجموعة الاختبارات الكاملة إلا بطلب صريح.

---

## 7. قواعد العمل مع Salim

- لا تعِد تصميم شاشات غير مطلوبة.
- لا تغيّر منطق التسعير أو الدفع أو المزامنة أو الهجرات إلا إذا انكشف خلل عقد حقيقي.
- لا تشغّل API ثانياً.
- لا تطبع الأسرار.
- التحقق من الواجهة يكون بسلوك حقيقي. لقطات IDE تقصّ أسفل الشاشة؛ قِس لوحة المفاتيح بـ `getBoundingClientRect` وليس بالصورة.
- بعد `Emulation.setDeviceMetricsOverride` أعد `Emulation.clearDeviceMetricsOverride`.
- التابلت لا يأخذ لوحة XtraPOS الكاملة.
- لوحة الأرقام المدمجة تبقى نافذة. لوحة الأحرف الكاملة تملأ الشاشة وارتفاعها 33% بالضبط.

---

## 8. ما لم يُطلب بعد

- إكمال قبول Meta القانوني ثم تجربة OTP واتساب حي قبل تبديل المزود.
- سيناريو بيع حي للسعر الاستثنائي ولقطة الدفع.
- نقل بحث الأصناف إلى معيار اللوحة الكاملة.
- إصلاح اختبار الإدخال السريع الذي يعدّ `variable-value` أربع مرات.
- لا يوجد commit لهذه التغييرات إلا إذا طلب Salim ذلك. شجرة git كانت متسخة بملفات الواجهة والخلفية والاختبارات وملفات `bin/obj` و`dist`. لا تُدخل مخرجات البناء في أي commit.
