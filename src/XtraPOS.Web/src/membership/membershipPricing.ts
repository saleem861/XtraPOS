export function membershipPricingCustomer(restrict: boolean, verified: boolean, customerId: string | null) {
  if (!customerId) return null;
  if (restrict && !verified) return null;
  return customerId;
}
