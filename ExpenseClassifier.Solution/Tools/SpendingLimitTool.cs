using System.ComponentModel;

namespace ExpenseClassifier.Tools;

public class SpendingLimitTool
{
    /// <summary>
    /// Returns company spending limits, expense thresholds, and approval requirement policies.
    /// </summary>
    /// <returns>The spending limit guideline text.</returns>
    [Description("Retrieves company spending thresholds and approval policies for financial compliance verification.")]
    public virtual string GetSpendingLimits()
    {
        return """
            Corporate Expense Spending Limits & Authorization Guidelines:

            1. Food:
               - Maximum allowance: ₦35,000 NGN or $50 USD per person per meal.
               - Meals exceeding this threshold require prior Department Head / Manager approval ("RequiresManagerApproval").
               - Lavish entertainment exceeding ₦100,000 NGN / $150 USD per person is prohibited without executive sign-off ("PolicyViolation").

            2. Transportation:
               - Standard rideshare (Bolt, Uber, Taxi): Maximum ₦25,000 NGN or $30 USD per trip.
               - Airport transit / inter-city travel: Maximum ₦50,000 NGN or $60 USD per trip.
               - Trips exceeding limits without business justification require manager review ("RequiresManagerApproval").

            3. Accommodation:
               - Standard business hotel: Maximum ₦180,000 NGN or $200 USD per night.
               - Luxury 5-star suites exceeding ₦300,000 NGN / $350 USD per night require executive sign-off ("RequiresManagerApproval").

            4. Software Subscriptions:
               - Individual monthly SaaS licenses under $100 USD (or equivalent ₦150,000 NGN) are pre-approved ("Compliant").
               - Annual licenses or software exceeding $100 USD / ₦150,000 NGN require IT department sign-off ("RequiresManagerApproval").

            5. Utilities & Office Supplies:
               - Standard monthly branch utility recharges up to ₦150,000 NGN / $100 USD are pre-approved ("Compliant").
               - Bulk office equipment above ₦200,000 NGN / $250 USD requires Procurement approval ("RequiresManagerApproval").

            Compliance Status Values:
            - "Compliant": Expense is within allowable spending limits and adheres to standard policy.
            - "RequiresManagerApproval": Expense exceeds standard threshold but is within justifiable managerial discretion.
            - "PolicyViolation": Expense clearly breaches allowable enterprise guidelines, contains prohibited items, or excessively exceeds executive limits.
            - "Unverifiable": Expense description does not provide sufficient amount or context to determine limit compliance.
            """;
    }
}
