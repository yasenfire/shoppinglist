using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Unity.VisualScripting;
using Unity.Netcode;
using UnityEngine;
using StbRectPackSharp;
using KaimiraGames;

namespace ShoppingList
{
    [BepInPlugin("yasenfire.shoppinglist", "ShoppingList", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;
        private Harmony m_harmony = new Harmony("yasenfire.shoppinglist");

        public static WeightedList<string> weightedBuyerList;

        private void Awake()
        {
            // Plugin startup logic
            Logger = base.Logger;
            Logger.LogInfo($"Plugin ShoppingList is loaded!");

            this.m_harmony.PatchAll();

            List<WeightedListItem<string>> weightedBuyerTypes = new()
                {
                    new WeightedListItem<string>("obsessive", 1),
                    new WeightedListItem<string>("loyal", 20),
                    new WeightedListItem<string>("standard", 79),
                };

            weightedBuyerList = new(weightedBuyerTypes);
        }

        [HarmonyPatch(typeof(CustomerController), nameof(CustomerController.SetupShoppingList))]
        class SetupShoppingListPatch
        {
            static bool Prefix(CustomerController __instance)
            {
                __instance.shoppingList.Clear();

                List<ProductSO> list = (from x in GameManager.Instance.itemDatabase
                                        where x is ProductSO
                                        select x).Cast<ProductSO>().ToList();

                List<WeightedListItem<ProductSO>> weightedProducts = new List<WeightedListItem<ProductSO>>();
                SeasonSO currentSeason = GameManager.Instance.GetCurrentSeason();
                EventSO currentEvent = GameManager.Instance.GetCurrentEvent();

                foreach (ProductSO item in list)
                {
                    float weight = 100f;
                    if (currentSeason == item.season) weight *= 1.2f;
                    else weight *= 0.9f;

                    float sellingPrice = GameManager.Instance.GetSellingPriceServer(item);
                    float recommendedPrice = GameManager.Instance.GetRecommendedPrice(item);

                    float sellingRecommendedRatio = recommendedPrice / sellingPrice;
                    if (sellingRecommendedRatio > 1f) weight *= Mathf.Lerp(1, 8, Mathf.Clamp(sellingRecommendedRatio, 1f, 2f) - 1f);
                    else weight *= Mathf.Lerp(0.1f, 1f, (Mathf.Clamp(sellingRecommendedRatio, 0.8f, 1f) - 0.8f) * 10f);

                    weight *= Random.Range(0.8f, 1.2f);

                    if (currentEvent is not null)
                    {
                        if (currentEvent.forbiddenProducts.Contains(item)) weight *= 0.1f;
                        if (currentEvent.products.Contains(item)) weight *= Random.Range(2.0f, 4f);
                    }

                    weightedProducts.Add(new WeightedListItem<ProductSO>(item, Mathf.RoundToInt(weight)));
                }

                WeightedList<ProductSO> weightedList = new WeightedList<ProductSO>(weightedProducts);

                List<ProductSO> list2 = (from x in (from x in FindObjectsOfType<Item>()
                                                    where x.itemSO is ProductSO && x.onStand.Value && x.amount.Value > 0
                                                    select x).DistinctBy((Item x) => x.itemSO.id)
                                         select x.itemSO).Cast<ProductSO>().ToList();

                float allMarketRatio = (float)list2.Count / (float)list.Count;

                string buyerType = weightedBuyerList.Next();

                if (buyerType == "obsessive")
                {
                    ProductSO item = weightedList.Next();

                    int num = Random.Range(4, Mathf.Min(item.amount, 24));
                    for (int i = 0; i < num; i += 1)
                    {
                        __instance.shoppingList.Add(item);
                    }
                    return false;
                }

                if (buyerType == "loyal")
                {
                    int maxItems = Mathf.RoundToInt(Mathf.Lerp(0, 18, allMarketRatio)) + 6;
                    int num = Random.Range(1, maxItems);

                    for (int i = 0; i < num; i += 1)
                    {
                        ProductSO item;
                        do
                        {
                            item = weightedList.Next();
                        } while (!list2.Contains(item));
                        __instance.shoppingList.Add(item);
                    }

                    return false;
                }

                int standardNum = Random.Range(1, 24);
                int itemsInMarket = 0;
                List<ProductSO> buyList = new List<ProductSO>();
                for (int i = 0; i < standardNum; i += 1)
                {
                    ProductSO item = weightedList.Next();
                    buyList.Add(item);

                    int increment = 1;

                    while (i < standardNum && Random.Range(0f, 1f) < item.amount / 100 / increment)
                    {
                        buyList.Add(item);
                        increment += 1;
                        i += 1;
                    }

                    if (list2.Contains(item)) itemsInMarket += increment;
                }

                if ((itemsInMarket / standardNum) >= 0.5f)
                {
                    for (int i = 0; i < buyList.Count; i += 1)
                    {
                        if (!list2.Contains(buyList[i])) continue;
                        __instance.shoppingList.Add(buyList[i]);
                    }
                }

                return false;
            }
        }

        [HarmonyPatch(typeof(Checkout), nameof(Checkout.SpawnProducts))]
        class CheckoutSpawnProductsPatch
        {
            static bool Prefix(Checkout __instance)
            {
                if (__instance.spawnedProducts)
                {
                    return false;
                }

                CheckoutEmployee employee = Traverse.Create(__instance).Field("checkoutEmployee").GetValue() as CheckoutEmployee;
                List<CustomerController> customers = Traverse.Create(__instance).Field("customers").GetValue() as List<CustomerController>;

                Transform initialSlot = __instance.productSlots.GetChild(0);
                Transform lastSlot = __instance.productSlots.GetChild(8);

                float areaWidth = Mathf.Abs(lastSlot.localPosition.x * 1000 - initialSlot.localPosition.x * 1000);
                float areaHeight = Mathf.Abs(lastSlot.localPosition.z * 1000 - initialSlot.localPosition.z * 1000) * 2;

                List<ProductSO> shoppingCart = customers[0].shoppingCart;
                shoppingCart.Sort((x, y) => x.id.CompareTo(y.id));

                Packer packer = new Packer(Mathf.RoundToInt(areaWidth), Mathf.RoundToInt(areaHeight));

                for (int i = 0; i < shoppingCart.Count; i += 1)
                {
                    Transform child = __instance.productSlots.GetChild(0);
                    ProductSO productSO = shoppingCart[i];
                    GameObject newItem = Instantiate(GameManager.Instance.checkoutItem, child.position, child.rotation);
                    Mesh renderer = productSO.pickupPrefab.GetComponentsInChildren<MeshFilter>()[0].sharedMesh;
                    Vector3 itemBounds = renderer.bounds.size;
                    packer.PackRect(Mathf.RoundToInt(itemBounds.z * 1000 * productSO.pickupPrefab.transform.localScale.z), Mathf.RoundToInt(itemBounds.x * 1000 * productSO.pickupPrefab.transform.localScale.x), (newItem, productSO.id));
                }

                foreach (PackerRectangle packRect in packer.PackRectangles)
                {
                    (GameObject item, long id) = ((GameObject, long))packRect.Data;
                    NetworkObject component = item.GetComponent<NetworkObject>();
                    component.Spawn(false);
                    component.GetComponent<CheckoutItem>().ServerSetupItem(id, __instance);
                    component.TrySetParent(__instance.gameObject.transform, true);
                    item.transform.localPosition += new Vector3((float)packRect.Rectangle.Y / 1000, 0f, (float)packRect.Rectangle.X / 1000 * -1);
                }
                __instance.spawnedProducts = true;
                if (employee != null)
                {
                    employee.Work();
                }
                return false;
            }
        }
    }
}