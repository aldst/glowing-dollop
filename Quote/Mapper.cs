using System;
using System.Collections.Generic;
using System.Linq;
using Quote.Contracts;
using Quote.Models;
using Quote.Models.Provider;

namespace Quote
{
    public class Mapper : IMapper
    {
        private const string DefaultOperatingHours = "12:00";
        private const string RemarkCommentType = "CONTRACT_REMARKS";
        private const string AdultType = "ADULT";
        private const string ChildType = "CHILD";
        private const string LanguageEs = "es";
        private const string LanguageEn = "en";
        public const int AdultAge = 25;
        private readonly ExternalProvider provider = ExternalProvider.HotelBedsTours;
        private readonly IMarginProvider marginProvider;

        public Mapper(IMarginProvider marginProvider)
        {
            this.marginProvider = marginProvider;
        }

        public ActivitiesDetailRequest Convert(TourQuoteRequest request)
        {
            if (request.adults <= 0)
            {
                request.adults = 1;
            }

            var paxes = new List<Pax>();
            for (var i = 0; i < request.adults; i++)
            {
                var pax = new Pax { Age = AdultAge };
                paxes.Add(pax);
            }

            if (!string.IsNullOrEmpty(request.kidValues))
            {
                foreach (var age in request.kidValues.Split(','))
                {
                    try
                    {
                        var kidAge = int.Parse(age);
                        var pax = new Pax { Age = kidAge };
                        paxes.Add(pax);
                    }
                    catch
                    {
                        var pax = new Pax { Age = 0 };
                        paxes.Add(pax);
                    }
                }
            }

            var newRequest = new ActivitiesDetailRequest
            {
                Code = request.TourCode,
                From = request.ArrivalDate,
                To = request.DepartingDate,
                Language = this.GetApitudLanguage(request.Language),
                Paxes = paxes
            };
            return newRequest;
        }

        public TourQuoteResponse Convert(TourQuoteRequest request, ActivitiesDetailResponse activity)
        {
            var currency = (Currency)Enum.Parse(typeof(Currency), activity.Activity.Currency);
            var exchangeRate = 1;
            var tour = this.GetTour(request, activity.Activity);
            var tourQuotes = new List<TourQuote>();
            var modalities = activity.Activity.Modalities.Where(w => w.Questions == null || !w.Questions.Any()).ToList();
            foreach (var modality in modalities)
            {
                var onlyAdults = this.GetAdultsOnly(modality);
                var quotes = this.GetTourQuotes(activity.Activity, modality, request, tour.ExternalCode, onlyAdults, currency);
                tourQuotes.AddRange(quotes);
            }

            if (request.RetrieveOptions.GetContracts)
            {
                tour.Contracts = tourQuotes.Select(s => s.ContractService.Contract).ToList();
            }

            var selectedServicesCodes = !string.IsNullOrEmpty(request.selectedServices) ? request.selectedServices.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList() : new List<string>();
            var activityServices = this.GetTourActivityService(activity.Activity, request.Language);
            if (selectedServicesCodes.Any())
            {
                activityServices = activityServices.Where(ts => selectedServicesCodes.Contains(ts.ServiceCode)).ToList();
                tourQuotes = tourQuotes.Where(w => selectedServicesCodes.Contains(w.ContractService.ServiceCode)).ToList();
            }

            TourCalculatedQuote calculatedQuote = null;
            if (tourQuotes.Any())
            {
                var baseSelectedQuote = tourQuotes.First();
                var margin = .25;
                if (request.RetrieveOptions.GetCalculatedQuote)
                {
                    calculatedQuote = new TourCalculatedQuote()
                    {
                        baseSelectedQuote = baseSelectedQuote,
                        allSelectedQuotes = tourQuotes,
                        maxPax = baseSelectedQuote.maxPax,
                        Tour = tour,
                        services = activityServices,
                        Margin = (double)margin,
                        EngineVersion = 1
                    };

                    foreach (var tourQuote in tourQuotes)
                    {
                        var modality = activity.Activity.Modalities.FirstOrDefault(f => f.Code == tourQuote.ContractService.ServiceCode);
                        this.SetTourCalculatedQuote(request, calculatedQuote, tourQuote, modality, exchangeRate);
                    }

                    calculatedQuote.CancellationPolicies = this.GetCancellationPolicies(request, calculatedQuote, currency, modalities);
                }
            }

            var result = new TourQuoteResponse()
            {
                TourQuotes = tourQuotes,
                BaseQuote = tourQuotes.FirstOrDefault(),
                calculatedQuote = calculatedQuote,
            };
            result.Tours.Add(tour);
            if (!result.HasQuote)
            {
                return result;
            }

            request.maxPax = request.adults + request.kids;
            result.QuoteInformationLists = GetInformationTourQuoteList(tourQuotes, activityServices);
            return result;
        }

        private void SetTourCalculatedQuote(TourQuoteRequest request, TourCalculatedQuote calculatedQuote, TourQuote tourQuote, Modality modality, decimal exchangeRate)
        {
            var rate = modality.Rates.FirstOrDefault();
            var rateDetail = rate?.RateDetails.FirstOrDefault();
            var totalNetAmount = rateDetail.TotalAmount.Amount * exchangeRate;
            var margin = this.marginProvider.GetMargin(modality.Code);
            var totalAmount = this.ApplyMarginToRate(totalNetAmount, margin);
            calculatedQuote.totalNetRate = (double)rateDetail.TotalAmount.Amount;
            calculatedQuote.totalAmount = (double)totalAmount;
            calculatedQuote.itemDetailAmount.Add(new rateIndexer() { indexCode = tourQuote.RateCode, value = (double)totalAmount });
            calculatedQuote.itemDetailNetAmount.Add(new rateIndexer() { indexCode = tourQuote.RateCode, value = (double)totalNetAmount });
            calculatedQuote.totalAdultNetRate += tourQuote.adultNetRate * request.adults;
            calculatedQuote.totalAdultAmount += tourQuote.adultRate * request.adults;
            var kidsAges = this.GetKidsAges(request.kids, request.kidValues);
            foreach (var kidAge in kidsAges)
            {
                var amount = tourQuote.Amounts.FirstOrDefault(f => f.PaxType == TourPaxType.Child && f.AgeFrom < kidAge && kidAge < f.AgeTo);
                calculatedQuote.totalKidNetRate += (double)amount.Cost;
                calculatedQuote.totalKidAmount += (double)amount.Price;
            }

            var remarks = modality.Comments.Where(w => w.Type == RemarkCommentType).Select(s => s.Text);
            calculatedQuote.QuoteRemarks.AddRange(remarks);
        }

        private List<CancelPolicy> GetCancellationPolicies(TourQuoteRequest request, TourCalculatedQuote calculatedQuote, Currency currency, List<Modality> modalities)
        {
            var policies = new List<CancelPolicy>();
            var rates = modalities.SelectMany(m => m.Rates);
            var rateDetails = rates.SelectMany(r => r.RateDetails);
            var operationDates = rateDetails.SelectMany(d => d.OperationDates);
            var cancellationPolicies = operationDates.SelectMany(o => o.CancellationPolicies);
            var cancellationPolicy = cancellationPolicies.OrderBy(o => o.DateFrom).FirstOrDefault();
            if (cancellationPolicy != null)
            {
                var policy = new CancelPolicy
                {
                    Amount = (decimal)calculatedQuote.totalNetRate,
                    DateFrom = cancellationPolicy.DateFrom.Date,
                    DateTo = request.DepartingDate.Date,
                    Currency = currency.ToString(),
                    Percentaje = 100
                };

                policies.Add(policy);
            }

            return policies;
        }

        private TourQuoteAddOnList GetInformationTourQuoteList(List<TourQuote> quotes, List<TourActivityService> services)
        {
            if (quotes == null || !quotes.Any() || services == null || !services.Any())
            {
                return new TourQuoteAddOnList();
            }

            var informationQuoteList = new TourQuoteAddOnList();
            var rateGroupPerDate = quotes.Where(r => r.ContractService.ActivityService.Type == TourServiceTypes.BASE).GroupBy(s => s.DtRate);
            foreach (var quotesPerDate in rateGroupPerDate)
            {
                var baseTourRate = quotesPerDate.ToList().OrderBy(q => q.Amount).FirstOrDefault();
                if (baseTourRate == null)
                {
                    continue;
                }

                var quotedDate = quotesPerDate.Key;
                var tourRates = quotes.Where(q => q.DtRate == quotedDate).ToList();
                if (!tourRates.Any())
                {
                    continue;
                }

                var quotedServicesCodes = tourRates.OrderBy(s => s.ContractService.ActivityService.Type).ThenBy(s => s.Amount)
                                                 .Select(g => g.ContractService.ActivityService.ServiceCode).ToList();
                var bestRateAmount = !baseTourRate.isAdultFree ? baseTourRate.Amount : baseTourRate.AmountKid[0];
                var servicesList = Math.Abs(bestRateAmount - 0) > 0.01 ? services.Where(g => quotedServicesCodes.Contains(g.ServiceCode) && g.IsActive).ToList() :
                                                                         services.OrderBy(s => s.Type).ToList();
                var servicesSelected = quotedServicesCodes.Any() ? quotedServicesCodes[0] : string.Empty;
                informationQuoteList.baseTourRate.Add(baseTourRate);
                informationQuoteList.tourRates.AddRange(tourRates);
                informationQuoteList.ServicesCodesList.Add(servicesList.Select(s => s.ServiceCode).ToList());
                informationQuoteList.servicesSelected.Add(servicesSelected);
                //informationQuoteList.reservationHourList.Add(PrecioyViajes.Service.TourService.SortReservationHours(string.Join("|", servicesList.Select(s => s.reservationTimes).ToArray())));
                var servicesAlreadyAdded = informationQuoteList.fullServiceList.Select(s => s.ServiceCode).ToList();
                informationQuoteList.fullServiceList.AddRange(servicesList.Where(s => !servicesAlreadyAdded.Contains(s.ServiceCode)).ToList());
                var sourceServicesIds = informationQuoteList.tourRates.OrderBy(s => s.ContractService.ActivityService.Type).ThenBy(s => s.Amount).Select(s => s.ContractService.ActivityService.ServiceCode).Distinct();
                var reorderedServices = sourceServicesIds.Select(serviceCode => informationQuoteList.fullServiceList.FirstOrDefault(s => s.ServiceCode == serviceCode)).Where(s => s != null).ToList();
                informationQuoteList.fullServiceList = reorderedServices;
            }

            return informationQuoteList;
        }

        private Tour GetTour(TourQuoteRequest request, Activity activity)
        {
            //var destinations = this.tourService.GetExternalDestinationStubsByObjectId(
            //    new Service.Model.ExternalDestinationStubRequest()
            //    {
            //        ExternalProvider = this.provider,
            //        ExternalCode = request.TourDestinationCode,
            //        GetDestinationMappings = true,
            //    });
            //Place destinationPlace = null;
            //var destination = destinations.FirstOrDefault(l => l.DestinationMappings != null && l.DestinationMappings.Count > 0);
            //var destinationMapping = destination.DestinationMappings[0];
            //destinationPlace = Tours.Utils.GetTourPlace(destinationMapping.ObjectDestinationId, (Content.Model.Enums.DestinationMappingType)destination.DestinationMappings[0].DestinationMappingType);
            var uri = (int)this.provider + "-" + activity.Code + "-" + "MCO";
            var durationType = this.GetDurationType(activity.Content.SegmentationGroups.FirstOrDefault(f => f.Code == 2));
            var includes = this.GetListIncludes(activity.Content.FeatureGroups);
            var include = this.GetIncludes(includes);
            var images = this.GetTourImages(activity.Name, activity.Content.Media);
            var imagePrincipal = images.FirstOrDefault();
            var categoryCodes = this.GetCategoryCodes(activity.Content.SegmentationGroups.FirstOrDefault(f => f.Code == 1));
            var activityServices = this.GetTourActivityService(activity, request.Language);
            var tourName = activity.Name;
            var serviceGroups = this.GetServiceGroups(tourName);
            var tour = new Tour()
            {
                operatingHours = DefaultOperatingHours,
                cityId = 0,
                //Place = destinationPlace,
                Language = request.Language,
                ExternalProviderId = this.provider,
                ExternalCode = activity.Code,
                Status = TourStatus.ACTIVE,
                ExternalDestinationCode = "MCO",
                ExternalQuoteSessionToken = string.Empty,
                ExternalLanguageCode = string.Empty,
                ComertialName = "HotelBeds",
                ReferenceAr = activity.Name,
                ReferenceBr = activity.Name,
                ReferenceCo = activity.Name,
                ReferenceEn = activity.Name,
                ReferenceEs = activity.Name,
                DescriptionAr = activity.Content.Description,
                DescriptionBr = activity.Content.Description,
                DescriptionCo = activity.Content.Description,
                DescriptionEn = activity.Content.Description,
                DescriptionEs = activity.Content.Description,
                shortDescriptionAr = activity.Content.Description,
                shortDescriptionBr = activity.Content.Description,
                shortDescriptionCo = activity.Content.Description,
                shortDescriptionEn = activity.Content.Description,
                shortDescriptionEs = activity.Content.Description,
                UriAr = uri,
                UriBr = uri,
                UriCo = uri,
                UriEn = uri,
                UriEs = uri,
                TourDuration = durationType,
                includesAr = include,
                includesBr = include,
                includesCo = include,
                includesEn = include,
                includesEs = include,
                Images = images,
                tourMainImages = imagePrincipal,
                ExternalCategoryCodes = categoryCodes,
                Services = activityServices,
                ServiceGroups = serviceGroups,
            };
            return tour;
        }

        private List<TourActivityService> GetTourActivityService(Activity activity, Models.Language language)
        {
            var services = new List<TourActivityService>();
            if (activity.OperationDays == null)
            {
                return services;
            }

            var operationDates = this.GetOperationDates(activity.OperationDays);
            var includes = this.GetListIncludes(activity.Content.FeatureGroups);
            var modalities = activity.Modalities.Where(w => w.Questions == null || !w.Questions.Any()).ToList();
            foreach (var modality in modalities)
            {
                var service = this.GetActivityService(modality, language, includes, operationDates);
                services.Add(service);
            }

            return services;
        }

        private List<DateTime> GetOperationDates(List<OperationDay> operationDays)
        {
            var operationDates = new List<DateTime>();
            foreach (var day in operationDays)
            {
                DayOfWeek dayOfWeek;
                switch (day.Code)
                {
                    case "MON":
                        dayOfWeek = DayOfWeek.Monday;
                        break;
                    case "TUE":
                        dayOfWeek = DayOfWeek.Tuesday;
                        break;
                    case "WED":
                        dayOfWeek = DayOfWeek.Wednesday;
                        break;
                    case "THU":
                        dayOfWeek = DayOfWeek.Thursday;
                        break;
                    case "FRI":
                        dayOfWeek = DayOfWeek.Friday;
                        break;
                    case "SAT":
                        dayOfWeek = DayOfWeek.Saturday;
                        break;
                    case "SUN":
                        dayOfWeek = DayOfWeek.Sunday;
                        break;
                    default:
                        continue;
                }

                var today = DateTime.Today.Date.AddDays(1);
                var daysUntilTuesday = ((int)dayOfWeek - (int)today.DayOfWeek + 7) % 7;
                var nextDate = today.AddDays(daysUntilTuesday);
                operationDates.Add(nextDate);
            }

            return operationDates;
        }

        private TourDurationType GetDurationType(SegmentationGroup segmentation)
        {
            if (segmentation != null)
            {
                var duration = segmentation.Segments.FirstOrDefault();
                if (duration != null)
                {
                    switch (duration.Code)
                    {
                        case 773:
                            return TourDurationType.HalfDayMorning;
                        case 772:
                            return TourDurationType.FullDay;
                        case 814:
                            return TourDurationType.Evening;
                        case 765:
                            return TourDurationType.MultiDay;
                        case 774:
                            return TourDurationType.HalfDayAfternoon;
                        case 771:
                            return TourDurationType.MultipleDepartures;
                    }
                }
            }

            return TourDurationType.None;
        }

        private bool GetAdultsOnly(Modality modality)
        {
            var childAmount = modality.AmountsFrom.FirstOrDefault(s => s.PaxType.Equals(ChildType) && s.Amount > 0);
            var onlyAdults = childAmount == null;
            return onlyAdults;
        }

        private List<int> GetKidsAges(int kids, string kidValues)
        {
            var kidsAges = kids > 0 ? kidValues.Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries).Select(a => int.Parse(a)).ToList() : new List<int>();
            return kidsAges;
        }

        private string GetIncludes(List<string> includes)
        {
            var include = includes.Aggregate((i, j) => i + "|" + j);
            return include;
        }

        private List<string> GetListIncludes(List<FeatureGroup> featureGroups)
        {
            var includes = new List<string>();
            foreach (var feature in featureGroups)
            {
                var i = feature.Included?.Select(s => s.Description);
                if (i != null)
                {
                    includes.AddRange(i);
                }
            }

            return includes;
        }

        private List<Models.Image> GetTourImages(string tourName, Media media)
        {
            var images = new List<Models.Image>();
            foreach (var i in media.Images)
            {
                var url = i.Urls.FirstOrDefault(f =>
                    f.SizeType.Equals("LARGE2", StringComparison.CurrentCultureIgnoreCase));
                if (url != null)
                {
                    var secureUrl = url.Resource.Replace("http://", "https://");
                    var image = new Models.Image
                    {
                        Description = tourName,
                        Name = tourName,
                        Primary = i.VisualizationOrder == 1,
                        UriLogoSmall = secureUrl,
                        UriLogoMedium = secureUrl,
                        UriMainSmall = secureUrl,
                        UriMainMedium = secureUrl,
                        UriMainLarge = secureUrl,
                        UrlSmall = secureUrl,
                        UrlMedium = secureUrl,
                        UrlLarge = secureUrl,
                        UrlExtraLarge = secureUrl,
                        URL = secureUrl,
                    };
                    images.Add(image);
                }
            }
            return images;
        }

        private List<int> GetCategoryCodes(SegmentationGroup categories)
        {
            var categoryCodes = new List<int>();
            if (categories != null)
            {
                categoryCodes = categories.Segments.Select(s => s.Code).ToList();
            }

            return categoryCodes;
        }

        private List<TourServiceGroup> GetServiceGroups(string tourName)
        {
            var serviceGroups = new List<TourServiceGroup>();
            serviceGroups.Add(new TourServiceGroup
            {
                nameEs = tourName,
                nameCo = tourName,
                nameAr = tourName,
                nameBr = tourName,
                nameEn = tourName,
            });
            return serviceGroups;
        }

        private TourActivityService GetActivityService(Modality modality, Models.Language language, List<string> includes, List<DateTime> operationDates)
        {
            var code = modality.Code;
            var service = new TourActivityService()
            {
                ServiceCode = code,
                Language = language,
                IsActive = true,
                Type = TourServiceTypes.BASE,
                ContractCode = modality.Contract.Name,
                NameAr = modality.Name,
                NameBr = modality.Name,
                NameCo = modality.Name,
                NameEn = modality.Name,
                NameEs = modality.Name,
                IncludedServices = includes,
                OperationDates = operationDates,
            };

            return service;
        }

        private List<TourQuote> GetTourQuotes(Activity activity,
            Modality modality, TourQuoteRequest request, string tourCode, bool onlyAdults,
            Currency providerCurrency)
        {
            var quotes = new List<TourQuote>();
            var margin = this.marginProvider.GetMargin(modality.Code);
            var exchangeRate = 1;
            try
            {
                var isRates = modality.Rates != null;
                var rate = modality.Rates?.FirstOrDefault();
                if (isRates && rate == null)
                {
                    throw new Exception("Rate is missing");
                }

                var rateDetail = rate?.RateDetails.FirstOrDefault();
                if (isRates && rateDetail == null)
                {
                    throw new Exception("Rate Detail is missing");
                }

                var paxAmounts = isRates ? rateDetail.PaxAmounts : modality.AmountsFrom;
                var adultPricingItem = paxAmounts.FirstOrDefault(f => f.PaxType.Equals(AdultType));
                if (adultPricingItem == null)
                {
                    throw new Exception("Adult rate is missing");
                }

                var adultRate = (double)(ApplyMarginToRate(adultPricingItem.Amount * exchangeRate, margin));
                var quote = new TourQuote()
                {
                    Currency = providerCurrency,
                    adultNetRate = (double)adultPricingItem.Amount,
                    adultRate = adultRate,
                    //kidRateCriteriType = (int)KidRateCriteria.AGE,
                    ForceKidLimits = false,
                    isAdultFree = adultPricingItem.Amount == 0,
                    onlyAdults = onlyAdults,
                    Amount = adultRate,
                    Cost = (double)adultPricingItem.Amount,
                    RateCode = isRates ? rateDetail.RateKey : modality.RateCode,
                    ContractCode = modality.Contract?.Name,
                    TourCode = tourCode ?? modality.Code,
                    DtRate = request.ArrivalDate,
                    Request = request,
                    maxPax = request.adults + request.kids,
                };

                if (request.RetrieveOptions.GetContracts)
                {
                    var currency = (Currency)Enum.Parse(typeof(Currency), activity.Currency);
                    var operationDates = this.GetOperationDates(activity.OperationDays);
                    var includes = this.GetListIncludes(activity.Content.FeatureGroups);
                    var contract = this.GetContract(modality, currency, request.Language, includes, operationDates);
                    quote.ContractService = contract.Services.FirstOrDefault();
                }

                quote.Amounts.Add(new TourAmount()
                {
                    AgeFrom = adultPricingItem.AgeFrom,
                    AgeTo = adultPricingItem.AgeTo,
                    PaxType = TourPaxType.Adult,
                    Cost = adultPricingItem.Amount,
                    Price = ApplyMarginToRate(adultPricingItem.Amount * exchangeRate, margin)
                });

                if (onlyAdults && request.kids > 0)
                {
                    throw new Exception("Child rate is missing");
                }

                if (onlyAdults == false)
                {
                    var childPricings = paxAmounts.Where(f => f.PaxType.Equals(ChildType) && f.Amount > 0)
                        .OrderBy(o => o.AgeFrom).ToList();
                    quote.kidAreUpTo = childPricings.OrderByDescending(o => o.AgeTo).FirstOrDefault().AgeTo;
                    quote.minCheckIn = childPricings.OrderBy(o => o.AgeFrom).FirstOrDefault().AgeFrom;
                    int child;
                    for (child = 0; child < 5; child++)
                    {
                        quote.kidNetRate.Add(0);
                        quote.kidRate.Add(0);
                        quote.kidIsFree.Add(false);
                        quote.kidCost.Add(0);
                        quote.AmountKid.Add(0);
                        quote.kidRangeStart.Add(0);
                        quote.kidRangeEnd.Add(0);
                    }

                    child = 0;
                    foreach (var childPricing in childPricings)
                    {
                        var cost = childPricing.Amount;
                        var price = childPricing.Amount > 0
                            ? ApplyMarginToRate(childPricing.Amount * exchangeRate, margin)
                            : 0;
                        quote.kidNetRate[child] = (double)cost;
                        quote.kidRate[child] = (double)price;
                        quote.kidIsFree[child] = cost == 0;
                        quote.kidCost[child] = (double)cost;
                        quote.AmountKid[child] = (double)price;
                        quote.kidRangeStart[child] = childPricing.AgeFrom;
                        quote.kidRangeEnd[child] = childPricing.AgeTo;
                        child++;

                        if (cost > 0)
                        {
                            quote.Amounts.Add(new TourAmount()
                            {
                                AgeFrom = childPricing.AgeFrom,
                                AgeTo = childPricing.AgeTo,
                                PaxType = TourPaxType.Child,
                                Cost = cost,
                                Price = price
                            });
                        }
                    }

                    quote.kidRangeSize = childPricings.Count;
                }

                quotes.Add(quote);
            }
            catch (Exception ex)
            {
                // Ignore
            }

            return quotes;
        }

        private TourContract GetContract(Modality modality, Currency currency, Models.Language language, List<string> includes, List<DateTime> operationDates)
        {
            var code = modality.Contract.Name;
            var contract = new TourContract
            {
                allChannelGroup = true,
                ContractCode = code,
                ExternalName = code,
                Tour = new Tour(),
                Currency = currency,
                IsActive = true,
            };
            var service = this.GetActivityService(modality, language, includes, operationDates);
            var serviceContract = new TourContractService()
            {
                IsActive = true,
                ContractCode = code,
                ServiceCode = service.ServiceCode,
                ActivityService = service,
                Contract = contract,
            };
            contract.Services.Add(serviceContract);
            return contract;
        }

        private string GetApitudLanguage(Models.Language language)
        {
            switch (language)
            {
                case Models.Language.english:
                    return LanguageEn;
                default:
                    return LanguageEs;
            }
        }

        private decimal ApplyMarginToRate(decimal rate, decimal margin)
        {
            return rate / (1 - margin);
        }
    }
}
