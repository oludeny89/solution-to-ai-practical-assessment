using System.ComponentModel;

namespace ExpenseClassifier.Tools;

public class CompanyPolicyTool
{
    /// <summary>
    /// Returns the official company policy guidelines for expense classification.
    /// </summary>
    /// <returns>The policy guideline text.</returns>
    [Description("Retrieves company policy rules that define how expense descriptions map to standard corporate GL categories.")]
    public virtual string GetPolicy()
    {
        return """
            Official Corporate Expense Classification Policy:

            1. Transportation:
               - Rideshares (Uber, Bolt, Lyft, local taxis)
               - Public transit, train/subway tickets, airport shuttle transfers
               - Vehicle fuel/petrol for approved business travel
               - Airline flight tickets and baggage fees

            2. Food:
               - Business meals with clients or partners
               - Team lunches and department dinners
               - Overtime meals during working hours at the office

            3. Accommodation:
               - Hotel and serviced apartment stays during approved business travel
               - Conference lodging bookings

            4. Utilities:
               - Office and branch electricity bills (e.g. IKEDC, EKEDC, prepaid tokens)
               - Office broadband and team internet data subscriptions (e.g. MTN, Airtel, Starlink)
               - Office water and waste disposal utilities

            5. Office Supplies:
               - Stationery, printing paper, pens, desk accessories
               - Minor peripheral office hardware (cables, adapters, keyboards)

            6. Software Subscriptions:
               - Developer tooling (JetBrains, GitHub, Visual Studio)
               - SaaS subscriptions (Slack, Zoom, Notion, Microsoft 365, AWS/Azure usage)

            Items not clearly matching these categories should be classified as 'Other'.
            """;
    }
}
