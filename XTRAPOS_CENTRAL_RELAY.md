# الترحيل إلى المركز وسجل الترحيلات

تاريخ الملف: 21 نوفمبر 2026، بتوقيت الخليل.  
المستودع: `https://github.com/saleem861/XtraPOS.git`.  
هذا الملف يصف الكود الحالي. إذا تعارض مع الملفات المذكورة فالكود هو المرجع.

هذا ليس ترحيل المخطط (`MigrationRunner`). ترحيل المخطط يغيّر الجداول. هذا الملف يصف نقل صفوف العمليات من `XtraPOS_Branch` إلى `XtraPOS_Central`، وكيف يُكتب ذلك في سجل الترحيلات.

الاتجاه المعاكس، من المركز إلى الفرع، لا يمر بهذا السجل. القيم الأولية لنقاط البيع تُسحب من `SyncChangeLog` بنوع `PosDeviceSettings` عبر `PosDeviceSettingsSyncWorker`. نسخ الأصناف والزبائن الأولى هي `SharedDataSync.BootstrapAsync` والتيار `CentralMaster`. أحداث المركز إلى الفرع تمر بـ `BranchFinancialInbox` و`FinancialInboxWorker`.

---

## 1. القاعدة

العملية تُحفظ على الفرع أولاً داخل نفس معاملة الحفظ. صف السجل يُدرج في المعاملة نفسها بحالة `Pending`. نجاح الشاشة لا ينتظر وصول المركز.

بعدها حلقة `CreditOutboxWorker` في عملية `XtraPOS.Api`:

1. تفتح اتصال الفرع. إذا لم يوجد تنتظر دقيقة.
2. تقرأ اتصال المركز من `SyncEndpoints` بدور `Central`. إذا لم يوجد تنتظر دقيقة.
3. تطبق ترحيلات مخطط المركز الناقصة مرة واحدة عبر `CentralMigrationRunner.ApplyAsync`. فشل المخطط لا يوقف الحلقة، والمحاولة التالية تعيد التطبيق.
4. تحذف من السجل الصفوف المكتملة الأقدم من مدة الاحتفاظ.
5. تأخذ أقدم صف حالته ليست `Completed` وليست `Cancelled`، مرتباً بـ `Serial`.
6. تنفذ نوع ذلك الصف فقط. الصفوف الأحدث تنتظر حتى يكتمل السابق أو يُلغى.
7. إذا نُفذت عملية تنتظر ثانيتين ثم تعيد الدورة. إذا لم يوجد شيء تنتظر 30 ثانية.

لا يوجد ترحيل إلى نسخة فرع بعيدة. `BranchRemoteSync.IsImplemented` يساوي `false`.

---

## 2. جدول السجل

الجدول `dbo.BranchOperationLog` على قاعدة الفرع. يُنشأ في الترحيل `039_BranchOperationLog`. النسخة الموحدة على المركز في `029_UnifiedSchemaObjects` لا تجعل المركز هو طابور التنفيذ. الطابور الذي تقرأه الحلقة هو جدول الفرع.

| العمود | المعنى |
|---|---|
| `Serial` | رقم متزايد. هو ترتيب التنفيذ |
| `OperationID` | معرّف العملية. فريد. إدراج نفس المعرّف مرة ثانية لا يفعل شيئاً |
| `ReferenceNumber` | `رمزالفرع-رقمالنقطة-yyyyMMddHHmmss-Serial` بتوقيت UTC |
| `OriginBranchCode` و`OriginBranchID` | الفرع الذي أنشأ العملية |
| `POSNumber` و`StationID` | نقطة البيع. إذا لم تُعرف النقطة فالرقم 0 |
| `ActorUserID` | المستخدم الذي نفّذ العملية |
| `OperationType` | نوع العملية، والقائمة في القسم 4 |
| `PayloadVersion` | دائماً 1 عند الإدراج الحالي |
| `Payload` | نص العملية. أحياناً معرّف فقط، وأحياناً JSON المبلغ والزبون |
| `Description` | النص العربي الذي يظهر في الشاشة |
| `SourceEntityType` و`SourceEntityID` | الجدول والصف المحلي الذي تُنسخ منه البيانات |
| `DestinationKind` | `Central` أو `Branch`. الإدراج الحالي إلى المركز يستخدم `Central` |
| `DestinationName` | الافتراضي للنقل إلى المركز هو «المركز» |
| `Status` | `Pending` أو `Executing` أو `Completed` أو `Blocked` أو `Cancelled` |
| `ResultMessage` | جملة نتيجة آخر محاولة |
| `AttemptCount` | يزيد عند `MarkAsync` |
| `CreatedAt` و`StartedAt` و`CompletedAt` و`LastAttemptAt` | أوقات UTC |
| `LeaseOwner` و`LeaseUntil` | يُفرغان عند تحديث الحالة. حجز التنفيذ الفعلي موجود على جداول الصندوق لا على هذا السجل |

الفهرس `IX_BranchOperationLog_Open` يغطي الصفوف `Pending` و`Executing` و`Blocked`.

`MarkAsync` لا يحفظ كل نص يُمرر إليه. `Completed` تبقى مكتملة، و`Cancelled` تبقى ملغاة، و`Executing` تبقى قيد التنفيذ. أي حالة أخرى، ومنها `Pending` و`ReconciliationRequired`، تُكتب في السجل `Blocked`. الصف المكتمل لا يُحدَّث مرة ثانية.

الاحتفاظ: المفتاح `OperationLogRetentionDays` في `dbo.SystemSettings`. إذا غاب أو كان أقل من 1 فالقيمة 30 يوماً. الحذف يمس الصفوف `Completed` التي مر على `CompletedAt` هذا العدد. الصفوف المعلقة والمتوقفة لا تُحذف.

عند إنشاء الجدول نُسخت الصفوف القديمة غير المكتملة من `BranchFinancialOutbox` و`BranchCollectionOutbox` و`BranchReversalOutbox` إلى السجل. الصفوف التي كانت `Completed` في تلك الصناديق لم تُنسخ.

---

## 3. ماذا يحدث عند التسجيل

`BranchOperationLog.EnqueueAsync` يعمل داخل معاملة الفرع:

1. إذا كان `OperationID` موجوداً يتوقف.
2. يقرأ `BranchCode` و`StationNumber`.
3. يدرج الصف بحالة `Pending` و`PayloadVersion = 1` ووجهة «المركز» إذا لم يُعطَ اسم آخر.
4. يكتب `ReferenceNumber` من الرقم الذي أخرجه الإدراج.

إذا كان هناك صف أقدم غير مكتمل وغير ملغى، `HasEarlierIncompleteAsync` يمنع اعتماد العملية الجديدة على المركز حتى لو حُفظت محلياً. الشاشة عندها تقول إن العملية محفوظة محلياً وقيد التأكيد، وليست ديناً أو تسديداً مؤكداً.

`IsExecutableHeadAsync` تمنع معالج الصندوق من تنفيذ عملية ليست رأس الطابور.

---

## 4. الأنواع وما يُنسخ إلى المركز

الحلقة تقرأ رأس الطابور ثم تختار المعالج:

| `OperationType` | متى يُسجل | ماذا يصل إلى المركز | رسالة الإكمال |
|---|---|---|---|
| `CompletedSale` | بعد حفظ بيع نقدي في `SalePostingService`، داخل معاملته، عبر `SaleCentralRelay.EnqueueCompletedAsync`. الوصف «ترحيل بيعة {الرقم}». الحمولة معرّف البيعة. المصدر `Sales` | `SaleCentralRelay.CopyAsync` ينسخ ما يلزم من الفرع والنقطة والجهاز والمستخدم والزبون ووردية الصندوق ثم `Sales` و`SaleItems` و`SalePayments`. لا يعيد إدراج صفاً موجوداً بنفس المفتاح | «تم ترحيل البيعة إلى المركز.» |
| `CreditSale` | داخل معاملة تأجيل الدفع مع `BranchFinancialOutbox` بنوع `FinalizeCredit` ودفتر الفرع. الوصف «تأجيل دفع بمبلغ …» | `CreditOutboxProcessor` يثبت التفويض والذمة على المركز ثم يعلّم الصندوق والسجل | «تم التنفيذ على المركز.» عند النجاح داخل المعاملة |
| `DebtCollection` | داخل معاملة التسديد مع `BranchCollectionOutbox` وصف `BranchDebtCollections`. الوصف «تسديد دين بمبلغ …» | `CreditCollectionService.FinalizeAsync` يوزع المبلغ على ذمم المركز المفتوحة | الإكمال من حالة الصندوق `Completed`. غير ذلك يُكتب `Blocked` |
| `CollectionReversal` | داخل معاملة عكس التسديد مع `BranchReversalOutbox` | `CreditReversalService` يعكس حركة التسديد على المركز | الإكمال من حالة الصندوق `Completed`. غير ذلك `Blocked` |
| `CustomerUpsert` | بعد حفظ الزبون أو تعديله في `CustomerMasterService`. الوصف «حفظ زبون» ثم الاسم. الحمولة معرّف الزبون | `BranchCardOutboxSync.RelayCustomerAsync` يحفظ بطاقة الزبون على المركز | «تم حفظ الزبون على المركز.» وإذا تعذر الاتصال: «المركز غير متاح لحفظ الزبون.» |
| `MembershipEnrollment` | عند تسجيل عضوية. المصدر `MembershipEnrollmentRequests` | `MembershipReplica.CopyEnrollmentAsync` | «تم نقل العضوية إلى المركز.» |
| `MembershipUpsert` | عند تغيير هوية العضوية. المصدر `CustomerMembershipIdentities` | `MembershipReplica.CopyIdentityToCentralAsync` | نفس جملة العضوية. إذا غاب الجدول على المركز (خطأ 208): «جدول العضوية غير موجود على المركز بعد.» |
| `CashDrawerCount` | عند تأكيد عد الجرار. الحمولة معرّف العد. المصدر `CashDrawerCounts` | `CashDrawerService.RelayLoggedAsync` ينسخ العد وسطور الفئات. إذا وُجد العد على المركز يُعلَّم `RelayedAt` محلياً | «تم ترحيل الجرد إلى المركز.» أو «المركز غير متاح. سيُعاد الترحيل.» |
| `CashDrawerDecision` | عند حفظ قرار المشرف. الحمولة معرّف العد لا معرّف القرار. المصدر ما زال `CashDrawerCounts` | ينسخ القرار إذا كان العد نفسه موجوداً على المركز | نفس جمل الجرد. الإكمال يعتمد `RelayedAt` |
| `DeviceSettings` | عند حفظ «القيم الأولية» لنقطة أو لجميع النقاط. الحمولة JSON النطاق والنقاط والقيم. المصدر `PosDeviceSettings` | `PosDeviceSettingsService.RelayLoggedAsync` يدمج القيم في `CentralPosDeviceSettings` ويكتب `SyncChangeLog` لتسحبه كل الفروع. خطأ 208 يوقف الصف | «تم ترحيل القيم الأولية إلى المركز (n نقطة).» |
| `EmployeeCashErrorAction` | عند مقاصة أو تسوية أو تأجيل أو ملاحظة في «أخطاء الموظفين». الحمولة معرّف الإجراء. المصدر `EmployeeCashErrorActions` | `EmployeeCashErrorService.RelayLoggedAsync` ينسخ الإجراء وبنوده إلى المركز إن لم يوجد. خطأ 208 (الترحيل 036 غير مطبّق) يوقف الصف | «تم ترحيل إجراء أخطاء الموظفين إلى المركز.» |
| `CustomerStarThresholds` | عند حفظ «نجوم الزبائن». الحمولة معرّف الصف. المصدر `CustomerStarThresholds` | `CustomerPurchasesService.RelayLoggedAsync` ينسخ الصف إلى المركز إن لم يوجد. خطأ 208 (الترحيل 037 غير مطبّق) يوقف الصف. على الجهاز المنفرد تكتمل فوراً | «تم ترحيل حدود نجوم الزبائن إلى المركز.» |
| `QuickItemSet` | عند حفظ «الأصناف السريعة» لفرع أو لكل الفروع. الحمولة معرّف القائمة. المصدر `QuickItemSets` | `QuickItemsService.RelayLoggedAsync` ينسخ القائمة وسطورها إلى المركز إن لم توجد، والفروع تسحبها كل دقيقة. خطأ 208 (الترحيل 038 غير مطبّق) يوقف الصف | «تم ترحيل الأصناف السريعة إلى المركز.» |

بيع نقدي مكتمل محلياً حتى لو تأخر المركز. تأجيل الدفع والتسديد والعكس تُحفظ محلياً أيضاً، لكن اعتمادها المالي على المركز يبقى معلقاً حتى يكتمل صفها. قرار الجرد لا يغيّر أرقام العد، والترحيل ينسخ القرار صفاً جديداً.

أخطاء `CompletedSale` التي توقف الصف: 208 جدول مفقود، 547 قيد، 2601 أو 2627 تكرار مفتاح. غيرها يكتب الرسالة في `ResultMessage` ويبقي الحالة كما هي حتى الدورة التالية. أخطاء الجرد أثناء النسخ لا تحذف الصف المحلي.

صناديق `BranchFinancialOutbox` و`BranchCollectionOutbox` و`BranchReversalOutbox` ما زالت تحمل الحمولة ومحاولات إعادة المحاولة. السجل هو ترتيب العرض والتنفيذ. إعادة المحاولة من الشاشة تعيد الصف إلى `Pending` وتعيد صندوقه إذا كانت حالته `ReconciliationRequired`.

تأخير إعادة محاولة صندوق الدين: ثانيتان مرفوعتان إلى عدد المحاولة زائد واحد، وبحد 60 ثانية بعد المحاولة الخامسة.

---

## 5. شاشة سجل الترحيل

المسار في البرنامج: الرئيسية، الإعدادات، «سجل الترحيل». يراها من يملك `System.Administer` فقط.

الشاشة `OperationLogScreen.tsx` تقرأ:

- `GET /api/operations/log` مع `from` و`to` و`destination`. نهاية اليوم تُحسب ببداية اليوم التالي.
- الصف الأول غير المكتمل هو `headSerial`.
- الوجهات قائمة الأسماء الموجودة، وتُعرض بلا تكرار.
- التحديث المباشر يعيد القراءة كل 4 ثوانٍ.

الحالات على الشاشة:

| الحالة | النص |
|---|---|
| `Pending` | بانتظار التنفيذ |
| `Executing` | قيد التنفيذ |
| `Completed` | تم التنفيذ |
| `Blocked` | متوقف |
| `Cancelled` | ملغى |

«إعادة المحاولة» تستدعي `POST /api/operations/log/{operationId}/retry`. تنجح للصف `Pending` أو `Executing` أو `Blocked`. تكتب النتيجة «بانتظار إعادة المحاولة.» ولا تتجاوز صفاً أقدم منها، لأن الحلقة ما زالت تنفذ الرأس فقط.

«حذف» يستدعي `DELETE /api/operations/log/{operationId}`. يحذف صف السجل فقط. إذا كان للصندوق المالي أو التسديد أو العكس صف غير مكتمل يُعلَّم `ReconciliationRequired` وجملته «حُذف أمر الترحيل من السجل.» الحذف لا يحذف البيعة ولا العد ولا الزبون من قاعدة الفرع ولا من المركز.

---

## 6. ما لا يدخله هذا السجل

- نسخ الكتالوج والزبائن من المركز إلى الفرع عند الإعداد (`initialSync` و`SharedDataSync`).
- صندوق الوارد `BranchFinancialInbox`.
- مزامنة `CentralPosReplica`.
- ترحيلات المخطط `DatabaseSchemaHistory` و`SchemaUpdateHistory`.
- أي عملية وُجهت إلى `BranchRemote`.

---

## 7. الملفات

- `src/XtraPOS.Api/Operations/BranchOperationLog.cs`
- `src/XtraPOS.Api/Credit/CreditOutboxWorker.cs`
- `src/XtraPOS.Api/Data/Migrations/039_BranchOperationLog.sql`
- `src/XtraPOS.Api/Endpoints/OperationLogEndpoints.cs`
- `src/XtraPOS.Web/src/components/OperationLogScreen.tsx`
- `src/XtraPOS.Api/Sales/SaleCentralRelay.cs`
- `src/XtraPOS.Api/Credit/CreditSaleCoordinator.cs`
- `src/XtraPOS.Api/Credit/CreditCollectionService.cs`
- `src/XtraPOS.Api/Credit/CreditReversalService.cs`
- `src/XtraPOS.Api/Sales/CashDrawerService.cs`
- `src/XtraPOS.Api/Customers/CustomerMasterService.cs`
- `src/XtraPOS.Api/Sync/MembershipReplica.cs`
- `src/XtraPOS.Api/Sync/BranchCardOutboxSync.cs`
