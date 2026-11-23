# الجرد (المرحلة 1 من وحدة الجرد والنواقص)

التصميم الكامل: مستند «تصميم وحدة الجرد والنواقص داخل XtraPOS» (23 نوفمبر 2026). هذه المرحلة تنقل «الجرد» من XtraPOS Inventory إلى XtraPOS على قاعدة الفرع.

## في شاشة البيع

- زر «الجرد» يفتح شاشة كاملة فوق البيع لمن يملك `Inventory.Count`. الإغلاق لا يلمس السلة.
- الشاشة الرئيسية تعرض بطاقات XtraPOS Inventory بنفس الترتيب، كل بطاقة لمن يملك صلاحيتها. «الجرد» فقط مفعّلة الآن، والبقية «قريباً».
- الفرع هو فرع الجهاز، فلا توجد خطوة «اختر الفرع».
- الأنواع: الأصناف المهمة، الدوري (12 صفحة، صفحة اليوم مختارة مسبقاً، قبل 16:00 وبعدها، الخميس بلا صفحة)، يدوي (بحث أو باركود).
- جرد اليوم غير المثبت لنفس الموظف ونفس النوع والصفحات: «متابعة» أو «جديد» (يمسح غير المثبت بعد تأكيد). جلسات الأيام السابقة تُهمل.
- **الجرد أعمى للكاشير**: بعد الحفظ يرى «المعدود» فقط. `Inventory.Review` (مشرف، مدير) يرى «المفترض» و«الفارق». الإخفاء على الخادم، لا في الواجهة فقط.
- «حفظ وخروج» لا يرسل شيئاً: كل عدّ يُحفظ لحظة الضغط على «حفظ».
- «تثبيت الجرد» يرحّل الأصناف المعدودة فقط.

## المخزون

- الجرد على صنف مالك المخزون فقط (`ItemUnits`). الأصناف الأم (`ParentItemID`) لا تُعدّ. باركود عبوة في الجرد اليدوي يُحوَّل إلى صنف الوحدة الأساسية.
- كمية النظام تُقرأ من `BranchStockBalances` لحظة حفظ العد (وتُعاد عند التعديل).
- التثبيت عبر `StockPostingService` بمصدر `InventoryCount`، في معاملة واحدة:
  - رصيد معروف: حركة `Delta` بالفرق (المعدود − كمية لحظة العد)، فلا تضيع المبيعات أثناء العد. `ObservedQuantity` = المعدود.
  - رصيد غير معروف: حركة `Absolute` تهيّئ الرصيد بالمعدود.
  - `SourceLineID` = سطر الجرد، فإعادة التثبيت لا تكرر الحركة.

## البيانات

- فرع `054_InventoryCounting`: `ItemInventorySettings`، `InventorySchedule`، `InventorySessions`، `InventorySessionPages`، `InventorySessionItems`، و11 صلاحية جديدة.
- مركز `039_CentralInventorySettings`: `ItemInventorySettings` و`InventorySchedule` والصلاحيات. **انسخ `XtraPOS_Central` احتياطياً قبل النشر.**
- كل دقيقة يسحب `PosDeviceSettingsSyncWorker` التصنيف والجدول من المركز (`InventoryCountService.PullSettingsAsync`). التصنيف لا يُحذف من المركز: إلغاؤه = `IsImportant = 0` وصفحة فارغة.
- التصنيف فارغ حتى يُستورد من `saleemPOSDB.dbo.InventorySettings` (المرحلة 7) أو يُعدَّل من «معالجة الأصناف» (المرحلة 2). بدونه لا يبدأ جرد المهم والدوري، ويعمل اليدوي.
- الجلسات تبقى على الفرع في هذه المرحلة. ترحيلها للمركز في المرحلة 3.

## الصلاحيات

| الصلاحية | كاشير | مشرف | مدير |
| --- | --- | --- | --- |
| `Inventory.Count` | ✓ | ✓ | ✓ |
| `Inventory.Review`, `Inventory.Define`, `Inventory.Classify`, `Inventory.CorrectQuantity`, `Shortages.Process`, `Shortages.FollowUp` | — | ✓ | ✓ |
| `Shortages.Approve`, `Shortages.Audit`, `Shortages.Report` | — | — | ✓ |
| `Shortages.Efficiency` (الكاشير تقاريره فقط، يُطبَّق في المرحلة 6) | ✓ | ✓ | ✓ |

## API

`/api/inventory/configuration`، `sessions/open`، `POST sessions`، `sessions/{id}/items`، `sessions/{id}/lookup`، `PUT sessions/{id}/count`، `POST sessions/{id}/finalize`. كلها بجلسة `XtraPOS.Auth` وصلاحية `Inventory.Count`، والجلسة لصاحبها فقط.
