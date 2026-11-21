# القيم الأولية للنقطة الحالية — التصميم والمزامنة

## الهدف
- المدير (Admin) والمشرف (Supervisor) يضبطان إعدادات **النقطة التي يعملان عليها فقط** من: الرئيسية ← الإعدادات ← «القيم الأولية للنقطة الحالية».
- موظف المركز (Admin) يضبط إعدادات **أي نقطة في أي فرع** من: الرئيسية ← الإعدادات ← «إعدادات نقاط الفروع».
- أول إعداد: `customerDisplay.enabled` (تفعيل شاشة الزبون)، والقيمة الافتراضية: غير مفعّل.

## التخزين
| المكان | الجدول | ملاحظات |
|---|---|---|
| الفرع | `POSDeviceConfigurations.ConfigurationJson["settings"]` | جدول 032 حسب وثيقة المواصفات §8. Migration 049 أضافت `SettingsCentralVersion` و`SettingsSyncedAtUtc`. |
| الفرع | `BranchDeviceSettingsOutbox` | **سجل الترحيلات** من الفرع إلى المركز. نفس نمط `BranchFinancialOutbox`. |
| الفرع | `DeviceSettingsInbox` | ما وصل من المركز وطُبِّق على نقاط هذا الفرع. |
| المركز | `CentralPosDeviceSettings` | سطر لكل DeviceID، مع `SettingsVersion` يزيد مع كل تعديل فعلي. |
| المركز | `PosDeviceSettingsAudit` | قيمة قبل وبعد لكل إعداد تغيّر فعلاً، ومن عدّله، والمصدر، ورقم الترحيل. |
| المركز | `PosDeviceSettingsReceipts` | استلام كل ترحيل من الفرع مرة واحدة فقط، حتى مع إعادة المحاولة. |
| المركز | `SyncChangeLog` | `EntityType = 'PosDeviceSettings'`، والـ Payload يحمل المستند كاملاً. |

شكل كل مفتاح داخل المستند:
```json
"customerDisplay.enabled": { "value": true, "updatedAtUtc": "2026-11-21T15:00:00.000Z",
  "updatedBy": "<UserID>", "updatedByName": "اسم المستخدم", "source": "Branch" }
```

## سجل الترحيلات (`BranchDeviceSettingsOutbox`)
- كل حفظ من الفرع يكتب الإعداد محلياً **وسطراً في الـ Outbox** داخل Transaction واحدة. السطر لا يُحذف بعد الترحيل.
- الأعمدة: `Status` (`Pending` أو `Completed` أو `ReconciliationRequired`)، و`AttemptCount`، و`LastAttemptAt`، و`NextRetryAt`، و`CompletedAt`، و`CentralVersion`، و`LastError`، و`DiagnosticID`، و`LeaseOwner`/`LeaseUntil`.
- الحجز (Lease) لمدة 45 ثانية بـ `UPDLOCK, READPAST`، فلا يرحّل عاملان السطر نفسه.
- عند الفشل: يُسجَّل الخطأ ورمز التشخيص (في `App_Data\logs\setup.log`)، والمحاولة التالية بعد 15ث، ثم 30ث، ثم 1د، وهكذا بحد أقصى 10 دقائق.
- محتوى غير صالح: `ReconciliationRequired` ولا يُعاد تلقائياً.
- المركز يسجّل `OutboxEventID` في `PosDeviceSettingsReceipts`، فإعادة المحاولة بعد انقطاع لا تُكرّر التدقيق ولا رفع الإصدار.
- الحالة تظهر أيضاً في مؤشر المزامنة بالهيدر (`SyncStatusStore`، العملية `pos-settings`).

## المسارات
1. **تعديل من الفرع:** حفظ محلي مع سطر Outbox، ثم محاولة ترحيل فورية لهذا السطر.
2. **العامل** `PosDeviceSettingsSyncWorker` يعمل كل دقيقة: يرحّل السطور الجاهزة (حتى 50 في الدورة، ويتوقف عند أول خطأ اتصال)، ثم يسحب من المركز.
3. **تعديل من المركز:** يُكتب في `CentralPosDeviceSettings` و`PosDeviceSettingsAudit` و`SyncChangeLog`. النقطة غير المعروفة مركزياً تُرفض (404).
4. **السحب:** يقرأ أسطر `PosDeviceSettings` بعد آخر نقطة تفتيش (`SyncCheckpoints`، Stream = `PosDeviceSettings`)، ويطبّق فقط ما يخص أجهزة موجودة في `dbo.Devices`، ويسجّلها في `DeviceSettingsInbox`.

## الواجهات
- `GET /api/device-settings/current/history`: سجل الترحيلات لهذه النقطة. يظهر أسفل شاشة «القيم الأولية للنقطة الحالية».
- `GET /api/device-settings/central/{deviceId}/history`: سجل التدقيق المركزي (قبل/بعد). يظهر في شاشة «إعدادات نقاط الفروع».

## حل التعارض
دمج **لكل مفتاح على حدة**: يفوز الأحدث `updatedAtUtc`، وعند التساوي يفوز المركز. الدمج تبادلي ومتكرر بأمان، لذلك:
- تعديل الفرع دون اتصال لا يضيع، ويُرفع عند عودة الاتصال.
- تعديلان مختلفان على مفتاحين مختلفين (واحد من الفرع وآخر من المركز) يبقيان معاً.
- تعديلان على المفتاح نفسه: الأحدث يفوز في الطرفين.

الوقت يُؤخذ من ساعة خادم الفرع أو المركز. انحراف الساعة بين الخادمين يؤثر فقط على تعديلين متقاربين جداً على المفتاح نفسه.

## الصلاحيات
- `Devices.ConfigureCurrent`: Admin وSupervisor (Migration 049 للفرع، و016 للمركز).
- `Devices.ManageAll`: Admin فقط.
- القراءة `GET /api/device-settings/current` متاحة لأي مستخدم مسجّل على النقطة، حتى تطبّق شاشة البيع الإعداد.

## إضافة إعداد جديد
أضف تعريفاً واحداً إلى `PosDeviceSettingsCatalog.All` في `Devices/PosDeviceSettingsDocument.cs`، ثم أضف عنصر التحكم في `DeviceInitialValuesScreen.tsx`. لا حاجة إلى Migration جديدة.

## قبل التشغيل
- Migration الفرع 033 تُطبَّق مع باقي Migrations الفرع.
- Migration المركز 016 **لا تُطبَّق على `XtraPOS_Central` الإنتاجية إلا بموافقة صريحة ونسخة احتياطية** (نفس قاعدة 015). قبل تطبيقها يعمل الحفظ محلياً، وتبقى التعديلات معلّقة بانتظار الرفع.
